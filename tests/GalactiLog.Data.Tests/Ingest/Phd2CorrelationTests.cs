using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Tests.TestSupport;
using Xunit;

// GalactiLog.Core.Phd2 and GalactiLog.Data.Entities each declare a Phd2Frame; this file seeds
// the stored rows, so it means the entity every time.
using Phd2Calibration = GalactiLog.Data.Entities.Phd2Calibration;
using Phd2Frame = GalactiLog.Data.Entities.Phd2Frame;

namespace GalactiLog.Data.Tests.Ingest;

/// <summary>
/// Task 5 brief sections 8.1 to 8.5: the never-overwrite guarantee of ruling F6, rig selection and
/// the multi-rig veto, pooling and the two coverage gates, the zone and pixel scale refusals, and
/// re-derivation. Every case states in its own remark what a failure of it looks like.
/// </summary>
public sealed class Phd2CorrelationTests
{
    private const string Rig = "RC8";
    private const string OtherRig = "FRA600";
    private const double PixelScale = 2.0;

    // 20 alternating samples of +/- 1 px at this scale are +/- 2.0 arcsec with a zero mean, so the
    // population sigma is exactly 2.0; the Dec axis is +/- 2 px, that is +/- 4.0 arcsec.
    private const double ExpectedRa = 2.0;
    private const double ExpectedDec = 4.0;
    private const double ExpectedTotal = 4.472136;

    private static readonly DateOnly Night = new(2026, 3, 1);
    private static readonly DateTime SessionStart = new(2026, 3, 2, 2, 0, 0);

    private static readonly IReadOnlyDictionary<string, Phd2ProfileEntry> EmptyMap =
        new Dictionary<string, Phd2ProfileEntry>();

    // ---- 8.1 The never-overwrite guarantee (ruling F6) ---------------------------------------

    /// <summary>
    /// A failure looks like: a measurement taken beside the frame is replaced by one derived from
    /// a log, silently, and the user has no way to tell it happened or to get the original back.
    /// </summary>
    [Fact]
    public void ACsvSourcedFrame_IsNeverOverwritten()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var csv = Guid.Empty;
        var neighbour = Guid.Empty;
        Seed(db, context =>
        {
            csv = AddImage(context, Rig, SessionStart, 40, source: "csv", value: 0.75);

            // A frame beside it that the same session does fill, so the csv row was reachable and
            // was passed over rather than merely never looked at.
            neighbour = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        var image = Read(db, csv);
        Assert.Equal("csv", image.GuidingRmsSource);
        Assert.Equal(0.75, image.GuidingRmsArcsec!.Value, 6);
        Assert.Equal(0.75, image.GuidingRmsRaArcsec!.Value, 6);
        Assert.Equal(0.75, image.GuidingRmsDecArcsec!.Value, 6);
        AssertFilled(Read(db, neighbour));
        Assert.Equal(1, result.Filled);
    }

    /// <summary>
    /// A failure looks like: the clear takes the CSV values out and the refill declines to replace
    /// them because the gates fail, so a re-ingest of an unrelated log empties a column that was
    /// correct.
    /// </summary>
    [Fact]
    public void ACsvSourcedFrame_SurvivesARederivePass_AndIsNotCounted()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40, source: "csv", value: 0.75);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        var image = Read(db, id);
        Assert.Equal("csv", image.GuidingRmsSource);
        Assert.Equal(0.75, image.GuidingRmsArcsec!.Value, 6);
        Assert.Equal(0, result.Cleared);
    }

    /// <summary>
    /// The other half of ruling F6: never-overwrite is about <c>csv</c> alone. A failure looks
    /// like: an edited profile map or a re-ingested log leaves yesterday's figure in place
    /// forever, because the pass refuses to touch a value it wrote itself.
    /// </summary>
    [Fact]
    public void AStalePhd2Value_IsRefreshedByARederive()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        var image = Read(db, id);
        Assert.Equal("phd2", image.GuidingRmsSource);
        Assert.Equal(ExpectedRa, image.GuidingRmsRaArcsec!.Value, 6);
        Assert.Equal(ExpectedDec, image.GuidingRmsDecArcsec!.Value, 6);
        Assert.Equal(ExpectedTotal, image.GuidingRmsArcsec!.Value, 6);
        Assert.Equal(1, result.Cleared);
        Assert.Equal(1, result.Filled);
    }

    /// <summary>
    /// A row carrying a guiding RMS and a NULL source is overwritable and clearable, by design.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spec 5.2's invariant is that a non-null RMS always carries a source: the CSV backfill of
    /// spec 7.4 stamps <c>csv</c> across the whole triple and this pass stamps <c>phd2</c>, so
    /// there is no third origin and no reachable row of this shape. The clear is therefore scoped
    /// by "carries a value at all" and the csv half is left to
    /// <c>Phd2Correlation.NeverOverwritesCsv</c> inside the write statement, which is ruling F6's
    /// one choke point. Scoping the clear by source as well would be a second per-caller guard
    /// that keeps the never-overwrite cases green with the choke point removed, which is exactly
    /// what F6 refuses.
    /// </para>
    /// <para>
    /// This case pins the consequence rather than leaving it implied: if a writer ever does
    /// produce a value with no source, this pass treats it as its own to clear and refill. A
    /// failure looks like the behaviour changing silently under a later edit, with the invariant
    /// still stated in the spec and no case saying what depends on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARowWithAValueAndNoSource_IsClearedAndRefilled_ByDesign()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40, source: null, value: 0.75);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        AssertFilled(Read(db, id));
        Assert.Equal(1, result.Cleared);
        Assert.Equal(1, result.Filled);
    }

    /// <summary>
    /// A failure looks like: a second write path added later for a good local reason, which is
    /// exactly what happened to the auth macro in the design lessons, with no test able to see it.
    /// </summary>
    [Fact]
    public void TheSourceScan_FindsExactlyOneWriterOfTheGuidingSource()
    {
        var correlation = SourceScan.Read("src/GalactiLog.Data/Ingest/Phd2Correlation.cs");
        var queries = SourceScan.Read("src/GalactiLog.Data/Queries/Phd2Queries.cs");

        // One statement in the file touches the images table at all, it is an ExecuteUpdate, and
        // the predicate is inside it. RederiveSessionTimes writes phd2_sessions and
        // phd2_calibrations through change tracking and must never appear in this count.
        Assert.Single(Regex.Matches(correlation, @"context\.Images"));
        Assert.Single(Regex.Matches(correlation, @"ExecuteUpdate\s*\("));
        Assert.Single(Regex.Matches(correlation, @"SetProperty\([^,]*GuidingRmsSource"));
        Assert.Single(Regex.Matches(correlation, @"NeverOverwritesCsv\s*="));
        Assert.Contains(".Where(NeverOverwritesCsv)", correlation, StringComparison.Ordinal);

        // Phd2Queries holds the reads and writes nothing.
        Assert.DoesNotContain("ExecuteUpdate", queries, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteDelete", queries, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", queries, StringComparison.Ordinal);

        // Across the shipped source, only the CSV backfill of spec 7.4 and this pass write the
        // column at all. That pair is the writer census line of TRACKING section 6 item 14.
        var writers = SourceScan.SourceFiles()
            .Where(path => Writes(SourceScan.StripComments(File.ReadAllText(path))))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "Phd2Correlation.cs", "ScanWriter.cs" }, writers);

        static bool Writes(string source)
            => Regex.IsMatch(source, @"\.GuidingRmsSource\s*=[^=]")
                || Regex.IsMatch(source, @"SetProperty\([^;]*GuidingRmsSource");
    }

    // ---- 8.2 Rig selection -------------------------------------------------------------------

    /// <summary>A failure looks like: the ordinary single-rig night, which is most nights, fills
    /// nothing.</summary>
    [Fact]
    public void ANightWithOneRigAndAMappedSession_IsFilled()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        AssertFilled(Read(db, id));
        Assert.Equal(1, result.Filled);
        Assert.Equal(1, result.FramesConsidered);
        Assert.Empty(result.UnattributedProfiles);
    }

    /// <summary>
    /// A failure looks like: a user who has not opened the Equipment tab yet sees no guiding at
    /// all and concludes the feature does not work.
    /// </summary>
    [Fact]
    public void ANightWithOneRigAndTheSoleUnmappedProfile_IsFilled()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, telescope: null, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        AssertFilled(Read(db, id));
        Assert.Equal(1, result.Filled);
        Assert.Empty(result.UnattributedProfiles);
    }

    /// <summary>
    /// A failure looks like: one of the two is picked arbitrarily and half the night's frames
    /// carry another rig's guiding.
    /// </summary>
    [Fact]
    public void TwoUnmappedProfilesOnOneNight_FillNothing_AndAreBothNamed()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, telescope: null, profile: "Rig A");
            AddGuidedSession(context, telescope: null, profile: "Rig B");
        });

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, id));
        Assert.Equal(0, result.Filled);
        Assert.Equal(new[] { "Rig A", "Rig B" }, result.UnattributedProfiles);
        Assert.Equal(1, result.UnattributedNights);
    }

    /// <summary>
    /// A failure looks like: the veto is absent and the guided rig's numbers land on the other
    /// rig's frames.
    /// </summary>
    [Fact]
    public void ANightSpanningTwoRigs_VetoesTheSoleUnmappedProfile()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var first = Guid.Empty;
        var second = Guid.Empty;
        Seed(db, context =>
        {
            first = AddImage(context, Rig, SessionStart, 40);
            second = AddImage(context, OtherRig, SessionStart, 40);
            AddGuidedSession(context, telescope: null, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, first));
        AssertUnfilled(Read(db, second));
        Assert.Equal(0, result.Filled);
        Assert.Equal(new[] { "Rig A" }, result.UnattributedProfiles);
    }

    /// <summary>
    /// The veto counts the FULL rig set. A failure looks like: the count is taken over the
    /// unfilled subset, so the night reads as single-rig, the veto does not fire, and rig B's
    /// frames get rig A's guiding. Every counter reads correct while it happens, which is why this
    /// case exists.
    /// </summary>
    [Fact]
    public void TheVetoCountsTheFullRigSet_IncludingARigWhoseFramesAllCarryCsv()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var covered = Guid.Empty;
        var exposed = Guid.Empty;
        Seed(db, context =>
        {
            covered = AddImage(context, Rig, SessionStart, 40, source: "csv", value: 0.75);
            exposed = AddImage(context, OtherRig, SessionStart, 40);
            AddGuidedSession(context, telescope: null, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        Assert.Equal("csv", Read(db, covered).GuidingRmsSource);
        AssertUnfilled(Read(db, exposed));
        Assert.Equal(0, result.Filled);
    }

    /// <summary>
    /// A failure looks like: a raw string comparison, so a rig whose profile was mapped before the
    /// grouping was created never matches anything.
    /// </summary>
    [Fact]
    public void ARigMappedUnderAnAliasSpelling_IsFilled()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            // The frame header says "RC8 f/8"; the profile map was written as "RC8".
            id = AddImage(context, "RC8 f/8", SessionStart, 40);
            AddGuidedSession(context, telescope: Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night], aliases: Aliases((Rig, ["RC8 f/8"])));

        AssertFilled(Read(db, id));
        Assert.Equal(1, result.Filled);
    }

    // ---- 8.3 Pooling and the gates -----------------------------------------------------------

    /// <summary>
    /// A failure looks like: PHD2 restarting mid-exposure leaves the frame with half its evidence
    /// or none. Each half alone is 10 samples spanning 45 percent of the exposure, which the
    /// coverage gate refuses; pooled they are 20 samples spanning 95 percent.
    /// </summary>
    [Fact]
    public void TwoSessionsOfOneRig_ArePooledIntoOneStream()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            var first = AddSession(context, SessionStart, Rig, "Rig A", PixelScale);
            AddFrames(context, first, 10);
            var second = AddSession(context, SessionStart.AddSeconds(20), Rig, "Rig A", PixelScale);
            AddFrames(context, second, 10);
        });

        var result = RunPass(db, [Night]);

        AssertFilled(Read(db, id));
        Assert.Equal(1, result.Filled);
    }

    /// <summary>A failure looks like: a second rig guiding badly in the same hour drags down the
    /// figure printed against this rig's frame.</summary>
    [Fact]
    public void TwoSessionsOfDifferentRigs_AreNeverPooled()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");
            var other = AddSession(context, SessionStart, OtherRig, "Rig B", PixelScale);
            AddFrames(context, other, 40, shape: _ => (100.0, 200.0, false));
        });

        // Both profiles are mapped, because since phase-review.md F3 the map is the only thing
        // that can say which rig a session describes. The seed's stored telescope columns agree
        // with it and are never read.
        RunPass(db, [Night], new Dictionary<string, Phd2ProfileEntry>
        {
            ["Rig A"] = new() { Telescope = Rig },
            ["Rig B"] = new() { Telescope = OtherRig },
        });

        AssertFilled(Read(db, id));
    }

    /// <summary>A failure looks like: a standard deviation over a handful of samples published as
    /// the sub's guiding RMS.</summary>
    [Fact]
    public void NineSamplesInTheWindow_LeaveNullColumns_AndCountBelowGate()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            // Offsets 1, 3, ... 17 fall inside an 18 second exposure: nine samples.
            id = AddImage(context, Rig, SessionStart, 18);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, id));
        Assert.Equal(1, result.BelowGate);
        Assert.Equal(0, result.Filled);
    }

    /// <summary>
    /// A failure looks like: twenty seconds of guiding at the start of a five-minute sub is
    /// published as that sub's RMS, which is a confident wrong number rather than an honest blank.
    /// </summary>
    [Fact]
    public void FortySamplesSpanningFortyPercentOfTheExposure_LeaveNullColumns()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 300);
            var session = AddSession(context, SessionStart, Rig, "Rig A", PixelScale);

            // Forty samples at offsets 1 to 118, which span 117 of the 300 second exposure.
            AddFrames(context, session, 40, firstOffset: 1.0, step: 3.0);
        });

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, id));
        Assert.Equal(1, result.BelowGate);
    }

    /// <summary>
    /// A failure looks like: the frame's figure reads several times worse than the session figure
    /// printed beside it on the same page, and the two describe the same guiding.
    /// </summary>
    [Fact]
    public void DropFramesAndFramesInsideAWindow_AreExcludedFromThePooledStream()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 80);
            var session = AddSession(
                context, SessionStart, Rig, "Rig A", PixelScale,
                events: """[{"type":"dither","t":10.0},{"type":"settle_done","t":30.0}]""");

            // Offsets 1, 3, ... 79. Frames 1 and 2 are DROP rows; frames 5 to 14 sit at offsets 11
            // to 29, inside the dither window. All twelve carry a wild value, so a figure that
            // included any of them could not be the clean one asserted below. The excluded set is
            // balanced, so the 28 survivors are still 14 positive and 14 negative.
            AddFrames(context, session, 40, shape: index =>
            {
                var dropped = index is 1 or 2;
                var windowed = index is >= 5 and <= 14;
                var magnitude = dropped || windowed ? 100.0 : 1.0;
                var sign = index % 2 == 0 ? 1.0 : -1.0;
                return (sign * magnitude, sign * magnitude * 2.0, dropped);
            });
        });

        RunPass(db, [Night]);

        AssertFilled(Read(db, id));
    }

    /// <summary>
    /// A failure looks like: a second window rule that is correct the day it is written and drifts
    /// the first time either side is tuned, after which a per-frame figure and the session figure
    /// beside it stop describing the same frames.
    /// </summary>
    [Fact]
    public void TheSourceScan_DeclaresNoWindowArithmeticOfItsOwn()
    {
        var correlation = SourceScan.Read("src/GalactiLog.Data/Ingest/Phd2Correlation.cs");

        Assert.Contains("Phd2Metrics.DitherSettleWindows(", correlation, StringComparison.Ordinal);
        Assert.Contains("Phd2Metrics.InWindows(", correlation, StringComparison.Ordinal);
        Assert.Contains("Phd2Metrics.PopulationSigma(", correlation, StringComparison.Ordinal);
        foreach (var kind in new[] { "dither", "settle_start", "settle_done", "settle_failed" })
        {
            Assert.DoesNotContain($"\"{kind}\"", correlation, StringComparison.Ordinal);
        }

        // Stronger than the literal sweep above, which a reimplementation naming the kinds through
        // Phd2EventTypes would slip past: the file declares no membership test and no standard
        // deviation of its own, so there is nothing left for a second rule to live in.
        Assert.DoesNotContain("Math.Sqrt", correlation, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(correlation, @"(private|static)\s+bool\s+InWindows"));
    }

    // ---- 8.4 The zone and the pixel scale ----------------------------------------------------

    /// <summary>
    /// A failure looks like: the session's wall clock is read as if it were UTC and a frame gets
    /// an RMS measured hours away from its exposure, stamped <c>phd2</c> like any correct value.
    /// That is the failure ruling F1 exists to refuse, and the web deployment it is taken from
    /// filled 172 frames that way.
    /// </summary>
    [Fact]
    public void ASessionWithNoZone_IsNeverRead_AndItsProfileIsNamed()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            var session = AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig Z", scale: PixelScale);
            AddFrames(context, session, 40);
        });

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, id));
        Assert.Equal(0, result.Filled);
        Assert.Equal(new[] { "Rig Z" }, result.TimezoneUnsetProfiles);
    }

    /// <summary>
    /// A failure looks like: an assumed scale, so an ASIAIR night's frames carry an invented
    /// arcsecond figure.
    /// </summary>
    [Fact]
    public void ASessionWithNoPixelScale_IsNeverCorrelated_AndItsProfileIsNamed()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            var session = AddSession(context, SessionStart, Rig, "Rig A", scale: null);
            AddFrames(context, session, 40);
        });

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, id));
        Assert.Equal(0, result.Filled);
        Assert.Equal(new[] { "Rig A" }, result.PixelScaleMissingProfiles);
    }

    /// <summary>An ASIAIR section names no profile at all, and a warning that names nobody is
    /// worse than one that says so.</summary>
    [Fact]
    public void ASessionWithNoProfileName_IsLabelledRatherThanLeftBlank()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            AddImage(context, Rig, SessionStart, 40);
            var session = AddSession(context, SessionStart, Rig, profile: null, scale: null);
            AddFrames(context, session, 40);
        });

        var result = RunPass(db, [Night]);

        Assert.Equal(new[] { "(no equipment profile)" }, result.PixelScaleMissingProfiles);
    }

    // ---- 8.5 Re-derivation and the trigger ---------------------------------------------------

    /// <summary>
    /// A failure looks like: an edited profile map leaves stale values behind forever, because
    /// incremental mode never revisits a frame that already has a value.
    /// </summary>
    [Fact]
    public void AnExplicitNightSet_ClearsThePhd2ValuesFirst()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9));

        var result = RunPass(db, [Night]);

        var image = Read(db, id);
        AssertUnfilled(image);
        Assert.Null(image.GuidingRmsSource);
        Assert.Equal(1, result.Cleared);
        Assert.Equal(0, result.Filled);
    }

    /// <summary>
    /// A failure looks like: every scan re-derives the whole catalogue, or a night with nothing to
    /// fill is visited anyway and its correct values are emptied.
    /// </summary>
    [Fact]
    public void APassWithNoNightSet_VisitsOnlyTheNightsNeedingFill_AndRederivesNothing()
    {
        var other = Night.AddDays(4);
        var otherStart = SessionStart.AddDays(4);
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var settled = Guid.Empty;
        var pending = Guid.Empty;
        Seed(db, context =>
        {
            settled = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9);
            AddGuidedSession(context, Rig, profile: "Rig A");

            pending = AddImage(context, Rig, otherStart, 40, night: other);
            var session = AddSession(context, otherStart, Rig, "Rig A", PixelScale, night: other);
            AddFrames(context, session, 40);
        });

        var result = RunPass(db, nights: null);

        Assert.Equal(1, result.Nights);
        Assert.Equal(0, result.Cleared);
        Assert.Equal(9.9, Read(db, settled).GuidingRmsArcsec!.Value, 6);
        AssertFilled(Read(db, pending));
    }

    /// <summary>
    /// The figures the <c>phd2_correlation_complete</c> details document of spec 10.9 needs are
    /// all on the result, so the caller that raises the event recomputes nothing. A failure looks
    /// like: the emitter counts rows again with its own query and the event disagrees with what
    /// the pass did.
    /// </summary>
    [Fact]
    public void TheResultCarriesEveryFigureTheCompleteEventNeeds()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            AddImage(context, Rig, SessionStart, 40);
            AddImage(context, Rig, SessionStart, 18);
            AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, [Night]);

        Assert.Equal(1, result.Nights);
        Assert.Equal(3, result.FramesConsidered);
        Assert.Equal(2, result.Filled);
        Assert.Equal(1, result.Cleared);
        Assert.Equal(1, result.BelowGate);
        Assert.False(result.Cancelled);
    }

    /// <summary>An already cancelled token stops the pass before it visits a night, and the
    /// figures say what was done rather than what was planned.</summary>
    [Fact]
    public void ACancelledToken_StopsThePassAndSaysSo()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = Phd2Correlation.Run(
            db.ConnectionString, [Night], EmptyMap, Aliases(), null, cancelled.Token);

        AssertUnfilled(Read(db, id));
        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Nights);
    }

    /// <summary>
    /// The nights a settings change invalidates: the union of the nights holding a
    /// <c>phd2</c>-sourced frame and the nights holding a guiding session. A failure looks like: a
    /// map edit re-derives the whole catalogue, or misses the night whose attribution it changed.
    /// </summary>
    [Fact]
    public void InvalidatedNights_IsTheUnionOfPhd2FramesAndGuidingSessions()
    {
        var other = Night.AddDays(4);
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 1.0);
            AddImage(context, Rig, SessionStart.AddDays(9), 40, night: Night.AddDays(9), source: "csv", value: 1.0);
            AddSession(context, SessionStart.AddDays(4), Rig, "Rig A", PixelScale, night: other);
        });

        Assert.Equal(new[] { Night, other }, Phd2Correlation.InvalidatedNights(db.ConnectionString));
    }

    /// <summary>
    /// A session whose own <c>session_date</c> lands a day off the frames it covers, which happens
    /// whenever the longitude behind it is unset, is still found: the lookup is a time overlap and
    /// not a date equality. A failure looks like: a whole night of guiding is invisible because a
    /// misconfigured longitude filed it under the wrong date.
    /// </summary>
    [Fact]
    public void ASessionFiledUnderTheWrongDate_IsStillFoundByTimeOverlap()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            var session = AddSession(
                context, SessionStart, Rig, "Rig A", PixelScale, night: Night.AddDays(1));
            AddFrames(context, session, 40);
        });

        RunPass(db, [Night]);

        AssertFilled(Read(db, id));
    }

    /// <summary>
    /// A session already running when the night's first exposure opens is found through the 24
    /// hour lookback. A failure looks like: the first hours of every night lose their guiding
    /// because PHD2 was started before the sequence.
    /// </summary>
    [Fact]
    public void ASessionStartedBeforeTheNightsFirstExposure_IsFoundThroughTheLookback()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            // The exposure opens four hours into a session that began the previous evening.
            id = AddImage(context, Rig, SessionStart.AddHours(4), 40);
            var session = AddSession(context, SessionStart, Rig, "Rig A", PixelScale);
            AddFrames(context, session, 40, firstOffset: 14401.0, step: 2.0);
        });

        RunPass(db, [Night]);

        AssertFilled(Read(db, id));
    }

    /// <summary>
    /// The profile map handed to the pass is the ONLY truth about which rig a session describes,
    /// which is what makes a map change worth re-running. A failure looks like: a user maps or
    /// unmaps a profile, the feed says the correlation ran, and nothing changes until the next
    /// full re-ingest.
    /// </summary>
    /// <remarks>
    /// REWRITTEN for phase-review.md F3. It used to run the first pass with the EMPTY map and
    /// expect nothing filled, on the strength of the stored <c>OtherRig</c> column vetoing
    /// attribution. Under F3 the stored column is never consulted, so that half inverted and is
    /// pinned as its own case below rather than renumbered here. Both halves of this case now
    /// turn on the map alone, which is what its name claims.
    /// </remarks>
    [Fact]
    public void TheProfileMapDecidesWhichRigASessionDescribes()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);

            // Stored against the other rig, because that is what the map said at ingest. It is
            // never consulted again: the seed keeps it precisely so that would show.
            AddGuidedSession(context, OtherRig, profile: "Rig A");
        });

        // Mapped to a rig this night did not shoot on. That is a decision the reader made, so
        // spec 7.6 step 2's sole-unmapped-profile fallback is vetoed and nothing is filled.
        Assert.Equal(0, RunPass(db, [Night], MapOf(OtherRig)).Filled);
        AssertUnfilled(Read(db, id));

        // Remapped onto the night's own rig, the same session fills the same frame.
        Assert.Equal(1, RunPass(db, [Night], MapOf(Rig)).Filled);
        AssertFilled(Read(db, id));
    }

    /// <summary>
    /// The behaviour change F3 makes to this pass, pinned so it is a decision on record rather
    /// than a silence: with no map at all a session resolves to NO rig whatever its stored
    /// <c>telescope</c> column says, so spec 7.6 step 2's sole-unmapped-profile fallback applies
    /// and the night's one rig is attributed.
    /// </summary>
    /// <remarks>
    /// In production the stored column IS the map's answer as it stood at ingest, so this differs
    /// from the Phase 15A behaviour only after a reader UNMAPS a profile, which is the case F3
    /// exists for. Before F3 the stored column acted as a veto here: an unmapped profile whose
    /// column still named some rig could never be attributed to another one, because no rescan
    /// rewrites that column.
    /// </remarks>
    [Fact]
    public void AnUnmappedProfilesStoredTelescope_NoLongerVetoesTheSoleUnmappedFallback()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, OtherRig, profile: "Rig A");
        });

        Assert.Equal(1, RunPass(db, [Night]).Filled);
        AssertFilled(Read(db, id));
    }

    /// <summary>
    /// F3's own case on this path: a profile the reader unmaps stops naming its rig, so the
    /// session it labelled is no longer attributed to that rig on a night that has another.
    /// </summary>
    [Fact]
    public void AProfileTheReaderUnmaps_StopsAttributingItsSessionToThatRig()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            // Two rigs on the night, so spec 7.6 step 3's multi-rig veto blocks the
            // sole-unmapped-profile fallback and the mapping is the only way in.
            id = AddImage(context, Rig, SessionStart, 40);
            AddImage(context, OtherRig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        Assert.Equal(1, RunPass(db, [Night], MapOf(Rig)).Filled);
        AssertFilled(Read(db, id));

        // The reader unmaps the profile: the entry leaves the map, which is what SetTelescope
        // does when a telescope is cleared and the entry carries nothing else. Red against the
        // stored-column fallback, under which the pass re-derived the very value it had written.
        Assert.Equal(0, RunPass(db, [Night]).Filled);
        AssertUnfilled(Read(db, id));
    }

    /// <summary>One profile "Rig A" mapped to one telescope, which is the shape the Equipment
    /// tab's panel writes.</summary>
    private static IReadOnlyDictionary<string, Phd2ProfileEntry> MapOf(string telescope)
        => new Dictionary<string, Phd2ProfileEntry> { ["Rig A"] = new() { Telescope = telescope } };

    // ---- Fix pass: the timezone warning scope (review P2-1) -----------------------------------

    /// <summary>
    /// Spec 10.9 names the profiles of sessions that met a frame this pass would have written. A
    /// failure looks like: every unzoned profile in the corpus is named on every scan forever,
    /// because one frame somewhere stays below a coverage gate and keeps its night in the
    /// incremental set, and an activity feed that cries wolf on every interval stops being read.
    /// </summary>
    [Fact]
    public void AnUnzonedProfileOnANightThePassDoesNotVisit_IsNotNamed()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");

            // Seven years away from the visited night, so no one-day widening can reach it.
            AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig Z", scale: PixelScale,
                startedLocal: SessionStart.AddYears(-7));
        });

        var result = RunPass(db, [Night]);

        Assert.Empty(result.TimezoneUnsetProfiles);
        Assert.Equal(1, result.Filled);
    }

    /// <summary>
    /// The warning is reachable from the path a zone edit actually takes: a settings-triggered
    /// pass over <c>InvalidatedNights</c> names the unzoned profile whose frames it went past.
    /// </summary>
    /// <remarks>
    /// A failure looks like: the user sets a zone for one rig, saves, the feed reports a
    /// correlation, and the rig that still has no zone is never named, because an unzoned session
    /// has a null <c>session_date</c> and neither half of the invalidation union can reach its
    /// night. The one message that would tell the user why that rig's guiding is missing cannot be
    /// written by the settings path at all, which is what the Task 6b capture showed.
    /// </remarks>
    [Fact]
    public void AnUnzonedProfileWithFramesOnItsNight_IsNamedByAPassOverInvalidatedNights()
    {
        var far = Night.AddYears(-3);
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            // The zoned rig, whose night the guide-night half of the union already reaches.
            AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");

            // The unzoned one, three years back, with frames of its own on the night its stored
            // local start falls on.
            AddImage(context, OtherRig, SessionStart.AddYears(-3), 40, night: far);
            AddSession(
                context, startedUtc: null, telescope: OtherRig, profile: "Rig Z",
                scale: PixelScale, startedLocal: SessionStart.AddYears(-3));
        });

        var nights = Phd2Correlation.InvalidatedNights(db.ConnectionString);
        var result = RunPass(db, nights);

        Assert.Equal(new[] { far, Night }, nights);
        Assert.Equal(new[] { "Rig Z" }, result.TimezoneUnsetProfiles);
        Assert.Equal(1, result.Filled);
    }

    /// <summary>
    /// The same union does not reach a night that holds no frame, so a guide log stored for a
    /// night the library has no images on is neither visited nor warned about.
    /// </summary>
    /// <remarks>
    /// A failure looks like: every unzoned profile in the corpus named on every settings save,
    /// whatever the pass could have written, which is the corpus-wide warning the Task 5a review
    /// already refused in its other form. The frame intersection is what keeps the widened local
    /// date from becoming one.
    /// </remarks>
    [Fact]
    public void AnUnzonedProfileWithNoFramesNearItsLocalDate_IsNotNamed()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            AddImage(context, Rig, SessionStart, 40);
            AddGuidedSession(context, Rig, profile: "Rig A");

            // Seven years back, with no image anywhere near it.
            AddSession(
                context, startedUtc: null, telescope: OtherRig, profile: "Rig Z",
                scale: PixelScale, startedLocal: SessionStart.AddYears(-7));
        });

        var nights = Phd2Correlation.InvalidatedNights(db.ConnectionString);
        var result = RunPass(db, nights);

        Assert.Equal(new[] { Night }, nights);
        Assert.Empty(result.TimezoneUnsetProfiles);
        Assert.Equal(1, result.Filled);
    }

    // ---- Fix pass: re-deriving session times (spec 7.6) ---------------------------------------

    /// <summary>
    /// The whole mechanism behind "set the zone later, then save". A failure looks like: the user
    /// configures a zone, the panel says a re-run was queued, the feed says the correlation ran,
    /// and nothing changes, because the guide log on disk is unchanged and the delta test of spec
    /// 10.3 never re-reads it.
    /// </summary>
    [Fact]
    public void AnUnzonedSession_GainsItsTimesWhenAZoneIsConfigured()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            durationS: 200.0));

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: EasternZone, longitude: Longitude),
            CancellationToken.None);

        var row = ReadSession(db, id);
        Assert.Equal(1, changed);
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), row.StartedAtUtc);
        Assert.Equal(new DateTime(2026, 3, 2, 7, 3, 20), row.EndedAtUtc);
        Assert.Equal(Night, row.SessionDate);

        // The naive wall clock is the stored source of truth and is never rewritten.
        Assert.Equal(SessionStart, row.StartedAtLocal);
    }

    /// <summary>
    /// A difference to null counts. A failure looks like: a zone the user removed leaves every
    /// session still carrying an instant derived under it, and the correlation goes on matching
    /// frames against a clock the user has said is wrong.
    /// </summary>
    [Fact]
    public void AZonedSession_LosesItsTimesWhenTheZoneIsRemoved()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, SessionStart, Rig, "Rig A", PixelScale, durationS: 200.0));

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(), CancellationToken.None);

        var row = ReadSession(db, id);
        Assert.Equal(1, changed);
        Assert.Null(row.StartedAtUtc);
        Assert.Null(row.EndedAtUtc);
        Assert.Null(row.SessionDate);
        Assert.Equal(SessionStart, row.StartedAtLocal);
    }

    /// <summary>
    /// A failure looks like: a zone corrected from one offset to another converges only for
    /// sessions ingested afterwards, so one corpus holds instants derived under two clocks with
    /// nothing saying which is which.
    /// </summary>
    [Fact]
    public void AChangedZone_MovesTheStoredInstants()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            durationS: 200.0));

        var eastern = Settings(globalZone: EasternZone, longitude: Longitude);
        Assert.Equal(1, Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, eastern, CancellationToken.None));
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), ReadSession(db, id).StartedAtUtc);

        // The profile's own zone wins over the global one, which is the resolution order of ruling
        // F1, resolved through the same member the ingest resolves it with.
        var perProfile = Settings(
            globalZone: EasternZone, longitude: Longitude, profileZones: [("Rig A", "UTC")]);
        Assert.Equal(1, Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, perProfile, CancellationToken.None));
        Assert.Equal(SessionStart, ReadSession(db, id).StartedAtUtc);
    }

    /// <summary>
    /// A failure looks like: every scan and every settings save rewrites every session row in the
    /// catalogue, so a re-derive that should be free churns the database and the change count tells
    /// a reader nothing.
    /// </summary>
    [Fact]
    public void NothingDiffers_WritesNoRowAndLeavesTheOtherColumnsByteIdentical()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            durationS: 200.0));

        var settings = Settings(globalZone: EasternZone, longitude: Longitude);
        Phd2Correlation.RederiveSessionTimes(db.ConnectionString, settings, CancellationToken.None);
        var first = ReadSession(db, id);

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, settings, CancellationToken.None);

        var second = ReadSession(db, id);
        Assert.Equal(0, changed);
        Assert.Equal(first.StartedAtLocal, second.StartedAtLocal);
        Assert.Equal(first.StartedAtUtc, second.StartedAtUtc);
        Assert.Equal(first.EndedAtUtc, second.EndedAtUtc);
        Assert.Equal(first.SessionDate, second.SessionDate);
        Assert.Equal(first.DurationS, second.DurationS);
        Assert.Equal(first.Telescope, second.Telescope);
        Assert.Equal(first.EquipmentProfile, second.EquipmentProfile);
        Assert.Equal(first.PixelScaleArcsec, second.PixelScaleArcsec);
        Assert.Equal(first.Events, second.Events);
    }

    /// <summary>
    /// The two transitions resolve exactly as <c>Phd2Metrics.LocalToUtc</c> resolves them, because
    /// they go through it. A failure looks like: a bare <c>TimeZoneInfo.ConvertTimeToUtc</c> here,
    /// which THROWS on the spring-forward gap and takes the whole settings save down with it, and
    /// which picks the second occurrence of a fall-back hour where the ingest picks the first, so
    /// one session moves by an hour depending on which path last touched it.
    /// </summary>
    [Fact]
    public void TheDstGapAndOverlap_ResolveAsLocalToUtcDoes()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var gap = Guid.Empty;
        var overlap = Guid.Empty;
        Seed(db, context =>
        {
            gap = AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
                startedLocal: new DateTime(2026, 3, 8, 2, 30, 0));
            overlap = AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
                startedLocal: new DateTime(2026, 11, 1, 1, 30, 0));
        });

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: EasternZone), CancellationToken.None);

        Assert.Equal(2, changed);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0), ReadSession(db, gap).StartedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0), ReadSession(db, overlap).StartedAtUtc);
    }

    /// <summary>
    /// The case the whole of ruling U4 exists for (spec 8.3 step 3, spec 7.6). A library with a
    /// zone configured and no longitude anywhere: the session's 21:42 local start belongs to the
    /// evening of the 13th, and before Phase 17 it was filed under the 14th because the guide side
    /// had no step 3 and fell straight to UTC-midnight grouping.
    /// </summary>
    /// <remarks>
    /// A failure looks like: the stored date stays on 2026-07-14 while the frames of that same
    /// evening sit on 2026-07-13, every consumer joins the two on strict session_date equality,
    /// and the guiding figures join nothing at all with no error and no warning.
    /// <para>
    /// No guide log is re-read: session_date is a derived column (spec 5.16) and the re-derive
    /// rebuilds it from the two stored wall clocks. The seeded log's path has never existed, so
    /// anything that opened it would throw, and its parsed_at is asserted unmoved.
    /// </para>
    /// </remarks>
    [Fact]
    public void AZoneAndNoLongitude_RederivesTheSessionDateOntoTheEveningsOwnNight()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            startedLocal: EveningLocalStart));

        string logPath;
        DateTime parsedAt;
        using (var before = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString)))
        {
            var log = before.Phd2Logs.Single();
            logPath = log.FilePath;
            parsedAt = log.ParsedAt;
        }

        Assert.False(File.Exists(logPath));

        // The zone is the only thing configured: no profile longitude, no observer longitude.
        Assert.Equal(1, Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: FixedMinusFourZone), CancellationToken.None));

        var row = ReadSession(db, id);
        Assert.Equal(new DateTime(2026, 7, 14, 1, 42, 0), row.StartedAtUtc);
        Assert.Equal(new DateOnly(2026, 7, 13), row.SessionDate);

        using var after = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        Assert.Equal(parsedAt, after.Phd2Logs.Single().ParsedAt);
        Assert.False(File.Exists(logPath));
    }

    /// <summary>
    /// The real-data shape (realdata-prep-report.md section 1.1, Phase 15B's R4 scenario 4): the
    /// frames carry their own SITELONG header, the guiding session carries no profile longitude,
    /// general.observer_longitude is unset and general.observer_timezone is a negative-offset zone.
    /// The frame and the session of the same evening must land on the same night.
    /// </summary>
    /// <remarks>
    /// A failure looks like the split U4 closes: the frame resolves through its own header and the
    /// session, having no step 3, falls to UTC-midnight grouping a day later. Both longitudes here
    /// are synthetic and no real site, path or rig name appears.
    /// </remarks>
    [Fact]
    public void AFrameWithItsOwnSiteLong_AndAZoneOnlySession_AgreeOnTheNight()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();

        // The frame's own header, spec 8.3 step 1. Near the zone's own 15-degrees-per-hour
        // longitude but not equal to it, which is what a real site is.
        var headers = new JsonObject { ["SITELONG"] = -59.0 };
        var frameCapture = new DateTime(2026, 7, 14, 2, 30, 0, DateTimeKind.Unspecified);
        var frameNight = GalactiLog.Core.Sessions.SessionDate.Compute(
            frameCapture,
            useImagingNight: true,
            GalactiLog.Core.Sessions.SessionDate
                .ResolveLongitude(headers, null, FixedMinusFourZone, frameCapture).Longitude);

        var image = Guid.Empty;
        var session = Guid.Empty;
        Seed(db, context =>
        {
            image = AddImage(context, Rig, frameCapture, 300.0, night: frameNight);
            session = AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
                startedLocal: EveningLocalStart);
        });

        Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: FixedMinusFourZone), CancellationToken.None);

        var stored = ReadSession(db, session);
        Assert.Equal(new DateOnly(2026, 7, 13), Read(db, image).SessionDate);
        Assert.Equal(Read(db, image).SessionDate, stored.SessionDate);
    }

    /// <summary>
    /// Carried item 54, in one row. An unzoned session whose wall clock runs 01:30 to 04:30 across
    /// the spring-forward gap is two real hours, and its stored <c>duration_s</c> is the three
    /// wall-clock hours an unzoned ingest measured. Configuring a zone re-derives both ends from
    /// the two stored wall clocks and re-derives the duration from the pair.
    /// </summary>
    /// <remarks>
    /// A failure looks like the reconstruction this replaces: 06:30 UTC plus the stored 10800
    /// seconds is 09:30 UTC, an hour late, and the session reads three hours long on every page
    /// that prints it, forever, with nothing stored to contradict it.
    /// </remarks>
    [Fact]
    public void AnUnzonedSessionSpanningTheSpringForwardGap_IsRederivedFromItsStoredLocalEnd()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            durationS: 10800.0,
            startedLocal: new DateTime(2026, 3, 8, 1, 30, 0),
            endedLocal: new DateTime(2026, 3, 8, 4, 30, 0)));

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: EasternZone, longitude: Longitude),
            CancellationToken.None);

        var row = ReadSession(db, id);
        Assert.Equal(1, changed);
        Assert.Equal(new DateTime(2026, 3, 8, 6, 30, 0), row.StartedAtUtc);
        Assert.Equal(new DateTime(2026, 3, 8, 8, 30, 0), row.EndedAtUtc);
        Assert.Equal(7200.0, row.DurationS);

        // Both wall clocks are the stored sources of truth and neither is rewritten.
        Assert.Equal(new DateTime(2026, 3, 8, 1, 30, 0), row.StartedAtLocal);
        Assert.Equal(new DateTime(2026, 3, 8, 4, 30, 0), row.EndedAtLocal);
    }

    /// <summary>
    /// A session with no stored local end has no end, and the re-derive invents none. That is the
    /// truncated section of spec 5.16, whose <c>duration_s</c> came from the last frame's time
    /// offset and which the re-derive has nothing better to offer.
    /// </summary>
    /// <remarks>
    /// The rule keys on the absent <c>ended_at_local</c> and not on the <c>truncated</c> flag: a
    /// row with no stored end is the one shape the arithmetic cannot serve, whatever the flag says.
    /// A failure looks like a fallback quietly reintroduced, so a section PHD2 never finished
    /// writing gains an end the log does not contain and <c>SessionsOverlapping</c> stops treating
    /// it as still running.
    /// </remarks>
    [Fact]
    public void ATruncatedSession_KeepsItsNullEnd_AndItsStoredDuration()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            durationS: 158.5, truncated: true));

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: EasternZone, longitude: Longitude),
            CancellationToken.None);

        var row = ReadSession(db, id);
        Assert.Equal(1, changed);
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), row.StartedAtUtc);
        Assert.Null(row.EndedAtUtc);
        Assert.Null(row.EndedAtLocal);
        Assert.Equal(158.5, row.DurationS);
    }

    /// <summary>
    /// The mixed shape the parser really produces and no case covered (task2d-review.md P3-2):
    /// <c>Phd2LogParser.cs:385</c> sets <c>Truncated</c> when it discards a corrupt CSV row, and
    /// that section may still carry its own <c>Guiding Ends</c> line and therefore a stored local
    /// end. The re-derive keys on the stored end and not on the flag, so this session gets its
    /// UTC end back.
    /// </summary>
    /// <remarks>
    /// A failure looks like the rule reading the <c>truncated</c> flag instead: every section
    /// with one bad CSV row loses its end forever, its <c>ended_at_utc</c> stays null, and
    /// <c>Phd2Queries.SessionsOverlapping</c> reads it as still running, so it is offered to every
    /// later night in the lookback window. The case could not be written at all until the seed
    /// helper stopped letting <c>truncated: true</c> overwrite an explicit <c>endedLocal</c>.
    /// </remarks>
    [Fact]
    public void ATruncatedSectionThatStillCarriesAnEndLine_IsGivenItsEndBack()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddSession(
            context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
            durationS: 158.5,
            endedLocal: SessionStart.AddSeconds(158.5),
            truncated: true));

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: EasternZone, longitude: Longitude),
            CancellationToken.None);

        var row = ReadSession(db, id);
        Assert.Equal(1, changed);
        Assert.True(row.Truncated);
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), row.StartedAtUtc);

        // The stored local end converted through the same zone as the start, never rebuilt from
        // the duration and never nulled because the flag is set.
        Assert.Equal(new DateTime(2026, 3, 2, 7, 2, 38, 500), row.EndedAtUtc);
        Assert.Equal(SessionStart.AddSeconds(158.5), row.EndedAtLocal);
    }

    /// <summary>
    /// One reader of the stored <c>events</c> document (task2-review.md P3-7, ruled in
    /// phase-review.md section 5). The behaviour that changed when the lenient
    /// <c>JsonDocument</c> walk went: a document with one structurally malformed entry now yields
    /// the EMPTY window list where the walk kept the good entries.
    /// </summary>
    /// <remarks>
    /// Pinned so it is a decision rather than a drift. Such a document is unreachable except by
    /// hand editing the database, because the ingest writes it and the column is required. The
    /// consequence, visible here, is that the dither and settle windows the malformed document
    /// described are no longer excluded, so every frame counts towards the window RMS.
    /// </remarks>
    [Fact]
    public void AMalformedEventsDocument_ExcludesNoWindowAtAll()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var wellFormed = Guid.Empty;
        var malformed = Guid.Empty;
        var idGood = Guid.Empty;
        var idBad = Guid.Empty;

        // The window covers every sample the exposure would otherwise use, so a document that is
        // read excludes everything and fills nothing, and a document that is not read excludes
        // nothing and fills.
        const string Window = """[{"type":"dither","t":0.0},{"type":"settle_done","t":200.0}]""";
        const string Broken = """[{"type":"dither","t":0.0},{"type":"settle_done","t":"oops"}]""";

        Seed(db, context =>
        {
            idGood = AddImage(context, Rig, SessionStart, 40);
            wellFormed = AddSession(
                context, SessionStart, Rig, "Rig A", PixelScale, events: Window);
            AddFrames(context, wellFormed, 40);

            idBad = AddImage(context, OtherRig, SessionStart, 40);
            malformed = AddSession(
                context, SessionStart, OtherRig, "Rig B", PixelScale, events: Broken);
            AddFrames(context, malformed, 40);
        });

        RunPass(db, [Night], new Dictionary<string, Phd2ProfileEntry>
        {
            ["Rig A"] = new() { Telescope = Rig },
            ["Rig B"] = new() { Telescope = OtherRig },
        });

        // The readable document's window swallows every sample, so there is nothing to average.
        AssertUnfilled(Read(db, idGood));

        // The malformed one reads as no events at all, so no sample is excluded and the frame
        // fills. Under the lenient walk it read as one dither and one unreadable entry, which
        // opened a window that ran to the last frame, and this frame stayed unfilled too.
        AssertFilled(Read(db, idBad));
    }

    /// <summary>
    /// Spec 7.6's one-day widening has ONE statement of it, <c>Phd2Queries.NightWindow</c>, and
    /// five callers had written it by hand (task2d-review.md P3-1, phase-review.md F12). This
    /// pins both bounds from outside, through the incremental mode's night set.
    /// </summary>
    /// <remarks>
    /// A failure looks like one of the five drifting: a session filed a day either side of the
    /// frames it covers stops being visited at all, and the night's guiding silently never
    /// arrives. The offsets are asymmetric on purpose, so a widening written one way round is not
    /// green by symmetry.
    /// </remarks>
    [Theory]
    [InlineData(-2, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void TheOneDayWidening_ReachesExactlyOneDayEitherSide(int offset, bool visited)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40);

            // The session is filed under a night `offset` days from the frames' own night, which
            // is what an unset longitude does. Its TIMES still overlap the exposure, so the only
            // thing that can keep it out is the widening.
            var session = AddSession(
                context, SessionStart, Rig, "Rig A", PixelScale, night: Night.AddDays(offset));
            AddFrames(context, session, 40);
        });

        // Incremental mode: the nights are chosen by Phd2Queries.NightsNeedingFill, which is one
        // of the five sites, rather than named by the caller.
        RunPass(db, nights: null);

        if (visited)
        {
            AssertFilled(Read(db, id));
        }
        else
        {
            AssertUnfilled(Read(db, id));
        }
    }

    /// <summary>
    /// The source-text half of the widening extraction: no sixth hand-written pair may appear in
    /// <c>Phd2Queries.cs</c>.
    /// </summary>
    [Fact]
    public void TheSourceScan_FindsOneStatementOfTheOneDayWidening()
    {
        var queries = SourceScan.Read("src/GalactiLog.Data/Queries/Phd2Queries.cs");

        // NightWindow states the rule once; NightWindowLocal is the SQL-side form of the same
        // window and is the only other place either offset may appear. Re-inlining any of the
        // five call sites moves one of these counts.
        Assert.Single(Regex.Matches(queries, @"AddDays\(-1\)"));
        Assert.Equal(2, Regex.Matches(queries, @"AddDays\(1\)").Count);
        Assert.Equal(3, Regex.Matches(queries, @"NightDates\(").Count - 1);
        Assert.Contains("NightWindowLocal(night)", queries, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property that matters, and the one this case was written to replace
    /// <c>ASessionSpanningTheFallBack_KeepsItsElapsedEnd_AndIsNotRewritten</c> with: an ingest and
    /// a re-derive of the same row cannot disagree. Both convert the two stored wall clocks through
    /// <c>Phd2Metrics.LocalToUtc</c> and round the duration the same way, so a re-derive over a
    /// freshly ingested log writes nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The replaced case guarded the same concern by arithmetic rather than by construction: while
    /// nothing stored the local end, rebuilding it as the re-derived start plus the stored elapsed
    /// seconds was the right answer, because elapsed seconds carry no DST term and a wall-clock
    /// rebuild would have landed an hour out across a transition. It stopped being right the moment
    /// the local end was stored, because the end is then converted rather than rebuilt and the row
    /// the case hand-seeded is one no ingest writes.
    /// </para>
    /// <para>
    /// The second section spans the fall-back overlap, where the local end is ambiguous and
    /// <c>LocalToUtc</c>'s fold-0 rule picks the first occurrence on both paths. A failure looks
    /// like the two paths using two rules for one row, so every settings save rewrites rows that
    /// were already right and the figure a reader saw yesterday moves by an hour for no reason they
    /// can see.
    /// </para>
    /// </remarks>
    /// <param name="longitude">The configured observer longitude, or null. The null row is the one
    /// configuration spec 8.3 step 3 answers in, and it is what pins that the ingest's call-site
    /// instant (<c>section.StartedAtLocal</c>) and the re-derive's (<c>row.StartedAtLocal</c>)
    /// produce the same longitude and therefore the same session_date. That agreement is what the
    /// whole of ruling Q7 shape 1 rests on, and without this row nothing exercised it: 0 changed
    /// rows is also the "a save does not rewrite rows that were already right" property.</param>
    [Theory]
    [InlineData(Longitude)]
    [InlineData(null)]
    public void ARederiveOverAFreshlyIngestedLog_AgreesWithTheIngest_AndWritesNothing(double? longitude)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var root = Directory.CreateTempSubdirectory("galactilog-phd2rederive-").FullName;
        try
        {
            var path = Path.Combine(root, "PHD2_GuideLog_2026-11-01_003000.txt");
            File.WriteAllText(path, FallBackLog);
            var info = new FileInfo(path);
            var settings = Settings(globalZone: EasternZone, longitude: longitude);

            var ingested = Phd2Ingest.Run(
                db.ConnectionString,
                [new DiscoveredFile(
                    path, info.Length, (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds)],
                settings, [root], false, null, null, null, CancellationToken.None);
            Assert.Equal(1, ingested.Ingested);

            List<Phd2Session> before;
            using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString)))
            {
                before = context.Phd2Sessions.OrderBy(s => s.SectionIndex).ToList();
            }

            Assert.Equal(2, before.Count);

            // Fold 0 on both ends of the ambiguous section: 01:30 and 01:45 local are still EDT.
            Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0), before[1].StartedAtUtc);
            Assert.Equal(new DateTime(2026, 11, 1, 5, 45, 0), before[1].EndedAtUtc);
            Assert.Equal(900.0, before[1].DurationS);

            var changed = Phd2Correlation.RederiveSessionTimes(
                db.ConnectionString, settings, CancellationToken.None);

            using var after = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
            var rows = after.Phd2Sessions.OrderBy(s => s.SectionIndex).ToList();
            Assert.Equal(0, changed);
            for (var index = 0; index < before.Count; index++)
            {
                Assert.Equal(before[index].StartedAtUtc, rows[index].StartedAtUtc);
                Assert.Equal(before[index].EndedAtUtc, rows[index].EndedAtUtc);
                Assert.Equal(before[index].EndedAtLocal, rows[index].EndedAtLocal);
                Assert.Equal(before[index].DurationS, rows[index].DurationS);
                Assert.Equal(before[index].SessionDate, rows[index].SessionDate);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Spec 7.6: the same re-derive runs over <c>phd2_calibrations</c>, whose spec 5.18 shape has
    /// two derived time columns and no end of any kind. A failure looks like: the calibration
    /// history stays empty on exactly the nights whose sessions the same save just fixed.
    /// </summary>
    [Fact]
    public void ACalibrationsTimes_AreRederivedOverItsTwoColumns()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddCalibration(context, profile: "Rig A"));

        var changed = Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, Settings(globalZone: EasternZone, longitude: Longitude),
            CancellationToken.None);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        var row = context.Phd2Calibrations.Single(calibration => calibration.Id == id);
        Assert.Equal(1, changed);
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), row.StartedAtUtc);
        Assert.Equal(Night, row.SessionDate);
        Assert.Equal(SessionStart, row.StartedAtLocal);
    }

    /// <summary>
    /// A failure looks like: a second copy of the resolution order that reads the machine's own
    /// zone when nothing is configured, which is the fallback ruling F1 refuses and which filled
    /// 172 frames with an RMS measured six hours away in the deployment it is taken from.
    /// </summary>
    [Fact]
    public void TheSourceScan_UsesTheIngestsOwnMembersAndNeverTheMachineZone()
    {
        var correlation = SourceScan.Read("src/GalactiLog.Data/Ingest/Phd2Correlation.cs");

        foreach (var member in new[]
        {
            "Phd2Profiles.ZoneResolver(",
            "Phd2Profiles.LongitudeResolver(",
            "Phd2Metrics.LocalToUtc(",
            "SessionDate.Compute(",
        })
        {
            Assert.Contains(member, correlation, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[]
        {
            "TimeZoneInfo.Local", "DateTime.Now", "DateTime.Today", "ToLocalTime(",
            "ToUniversalTime(", "ConvertTimeToUtc(", "SpecifyKind(",
        })
        {
            Assert.DoesNotContain(forbidden, correlation, StringComparison.Ordinal);
        }

        // Both wall clocks are stored sources of truth: read here, written nowhere. A failure looks
        // like a well-meaning normalisation writing a cleaned-up wall clock back to the column the
        // whole re-derive rests on being immutable, which no other case would catch because every
        // derived value would still be self-consistent.
        Assert.Empty(Regex.Matches(correlation, @"StartedAtLocal\s*=[^=]"));
        Assert.Empty(Regex.Matches(correlation, @"EndedAtLocal\s*=[^=]"));
    }

    /// <summary>
    /// The nights a re-derive makes reachable must be among the nights the next pass visits. A
    /// failure looks like: the zone is fixed, the sessions gain their instants, and the frames they
    /// cover stay blank because nothing told the correlation those nights exist.
    /// </summary>
    [Fact]
    public void ARederivedSessionsNight_ReachesRunThroughInvalidatedNights()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        Seed(db, context =>
        {
            // The frame's capture time is a UTC instant and the session's is a wall clock five
            // hours behind it, which is the whole point: until the re-derive runs, the session has
            // no UTC instant at all and the two cannot be compared.
            AddImage(context, Rig, SessionStart.AddHours(5), 40);
            var session = AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
                durationS: 200.0);
            AddFrames(context, session, 40);
        });

        // Before the re-derive the session has no UTC instant and no session_date, so neither the
        // phd2-frame half of the union nor the guide-night half can name its night. It is reached
        // all the same, through its stored local date and the frames sitting on that night, which
        // is what makes the missing-zone warning reachable from a settings save at all.
        var before = Phd2Correlation.InvalidatedNights(db.ConnectionString);
        Assert.Equal(new[] { Night }, before);

        // And a pass over it fills nothing and says why, which is the state the user is in until
        // the zone is configured.
        var unzoned = RunPass(db, before);
        Assert.Equal(0, unzoned.Filled);
        Assert.Equal(new[] { "Rig A" }, unzoned.TimezoneUnsetProfiles);

        var settings = Settings(globalZone: EasternZone, longitude: Longitude);
        Assert.Equal(1, Phd2Correlation.RederiveSessionTimes(
            db.ConnectionString, settings, CancellationToken.None));

        var nights = Phd2Correlation.InvalidatedNights(db.ConnectionString);
        Assert.Equal(new[] { Night }, nights);

        // The whole flow in the order the callers run it: re-derive, invalidate, correlate.
        Assert.Equal(1, RunPass(db, nights).Filled);
    }

    /// <summary>
    /// Review item 6, the <c>ScanWriter</c> rewrite: a frame whose guiding columns were just
    /// emptied by a header re-ingest is an unfilled frame again, so its night is in the incremental
    /// set and the same scan's correlation refills it. A failure looks like: a re-scan silently
    /// drops the guiding figure off every frame it rewrites and nothing puts it back.
    /// </summary>
    [Fact]
    public void ANightWhoseFrameWasRewrittenToANullRms_IsVisitedInIncrementalMode()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            // Exactly the state ScanWriter leaves behind: all four guiding columns null.
            id = AddImage(context, Rig, SessionStart, 40, source: null, value: null);
            AddGuidedSession(context, Rig, profile: "Rig A");
        });

        var result = RunPass(db, nights: null);

        Assert.Equal(1, result.Nights);
        Assert.Equal(1, result.Filled);
        AssertFilled(Read(db, id));
    }

    // ---- The clear-side hold-out for an unzoned night (spec 7.6, carried item 55) --------------

    /// <summary>
    /// A night whose only guiding session has no resolved zone is left alone by the re-derive
    /// clear. It is refilled normally afterwards and fills nothing, because an unzoned session
    /// contributes no samples.
    /// </summary>
    /// <remarks>
    /// A failure looks like the behaviour that shipped before the hold-out: a user who clears a
    /// timezone, or whose per-profile zone stops loading after a platform update, watches every
    /// guiding figure in the library empty itself on the next save and stay empty until they put
    /// the zone back.
    /// </remarks>
    [Fact]
    public void ANightWhoseOnlyGuidingIsUnzoned_IsHeldOutOfTheClear()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var stored = Guid.Empty;
        var csv = Guid.Empty;
        Seed(db, context =>
        {
            stored = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9);
            csv = AddImage(context, Rig, SessionStart, 40, source: "csv", value: 0.75);
            AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
                durationS: 200.0);
        });

        var result = RunPass(db, [Night]);

        var image = Read(db, stored);
        Assert.Equal("phd2", image.GuidingRmsSource);
        Assert.Equal(9.9, image.GuidingRmsArcsec!.Value, 6);
        Assert.Equal(9.9, image.GuidingRmsRaArcsec!.Value, 6);
        Assert.Equal(9.9, image.GuidingRmsDecArcsec!.Value, 6);
        Assert.Equal(0, result.Cleared);

        // Untouched twice over, by the hold-out and by NeverOverwritesCsv. The hold-out did not
        // become a second place where ruling F6 is decided.
        var measured = Read(db, csv);
        Assert.Equal("csv", measured.GuidingRmsSource);
        Assert.Equal(0.75, measured.GuidingRmsArcsec!.Value, 6);
    }

    /// <summary>
    /// A night that has a zoned session as well is cleared and refilled normally, from the zoned
    /// session alone.
    /// </summary>
    /// <remarks>
    /// A failure looks like the hold-out written as "any unzoned candidate holds the night", so a
    /// single unmapped profile anywhere near a date freezes that night's values against every
    /// correction the user makes afterwards.
    /// </remarks>
    [Fact]
    public void ANightMixingAZonedSessionWithAnUnzonedOne_IsClearedAndRefilled()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9);
            AddGuidedSession(context, Rig, profile: "Rig A");
            AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig B", scale: PixelScale,
                durationS: 200.0);
        });

        var result = RunPass(db, [Night]);

        AssertFilled(Read(db, id));
        Assert.Equal(1, result.Cleared);
        Assert.Equal(1, result.Filled);
    }

    /// <summary>
    /// A night with no guiding session of either kind is cleared normally. That is the state after
    /// a guide log is deleted from disk.
    /// </summary>
    /// <remarks>
    /// A failure looks like the hold-out written as "no zoned session holds the night", dropping
    /// the unzoned-candidate half, so a deleted guide log's numbers survive forever on every night
    /// it used to cover.
    /// </remarks>
    [Fact]
    public void ANightWithNoGuidingSessionAtAll_IsClearedNormally()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context => id = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9));

        var result = RunPass(db, [Night]);

        AssertUnfilled(Read(db, id));
        Assert.Equal(1, result.Cleared);
    }

    /// <summary>
    /// The unzoned half is a date test widened by one day either side, and nothing else: the three
    /// sessions below all start at 23:00, which overlaps none of the night's exposures.
    /// </summary>
    /// <remarks>
    /// A failure looks like the widening written as an equality on the local date, which is the one
    /// thing spec 7.6 says it cannot be: a wall clock and an imaging night can legitimately fall on
    /// different calendar dates, and the hold-out then misses exactly the sessions it exists for.
    /// </remarks>
    [Theory]
    [InlineData(2026, 2, 28, true)]
    [InlineData(2026, 3, 2, true)]
    [InlineData(2026, 2, 27, false)]
    // The upper bound pinned from outside (task2d-review.md P3-3). Without this row a widening to
    // to.AddDays(2) passes every other row, so the bound was pinned in one direction only.
    [InlineData(2026, 3, 4, false)]
    public void AnUnzonedSessionsLocalDate_HoldsTheNightOnlyWithinOneDayEitherSide(
        int year, int month, int day, bool held)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var id = Guid.Empty;
        Seed(db, context =>
        {
            id = AddImage(context, Rig, SessionStart, 40, source: "phd2", value: 9.9);
            AddSession(
                context, startedUtc: null, telescope: Rig, profile: "Rig A", scale: PixelScale,
                durationS: 200.0, startedLocal: new DateTime(year, month, day, 23, 0, 0));
        });

        var result = RunPass(db, [Night]);

        var image = Read(db, id);
        if (held)
        {
            Assert.Equal(0, result.Cleared);
            Assert.Equal(9.9, image.GuidingRmsArcsec!.Value, 6);
        }
        else
        {
            Assert.Equal(1, result.Cleared);
            AssertUnfilled(image);
        }
    }

    // ---- Fixtures ----------------------------------------------------------------------------

    private const string EasternZone = "America/New_York";

    /// <summary>A fixed UTC-04:00 zone with no saving at all, which is the fixture convention
    /// (fixtures/README.md): a DST zone would read a different offset on the fixture date and the
    /// two sides of the U4 cases would be comparing different arithmetic, not the same rule.
    /// </summary>
    private const string FixedMinusFourZone = "America/La_Paz";

    /// <summary>21:42 local on the evening of the 13th, the shape realdata-prep-report.md section
    /// 1.1 names: at UTC-04:00 it is 01:42 UTC on the 14th, so UTC-midnight grouping files it a
    /// day late and step 3 files it on the evening it belongs to.</summary>
    private static readonly DateTime EveningLocalStart = new(2026, 7, 13, 21, 42, 0);

    // The CSV header is compared byte for byte by the parser (spec 7.6), so it is written out in
    // full rather than assembled.
    private const string GuideCsvHeader =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance," +
        "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode";

    // Two sections on the night the clocks go back in the Eastern zone: the first unambiguous, the
    // second running 01:30 to 01:45, a wall clock that happens twice. Both paths resolve it fold 0.
    private const string FallBackLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2026-11-01 00:29:00
        Guiding Begins at 2026-11-01 00:30:00
        Equipment Profile = Rig A
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        2,1.000,"Mount",0.10,0.20,-0.400,-0.300,0.100,0.100,10,E,12,S,0,0,12000.0,25.0,0
        Guiding Ends at 2026-11-01 00:45:00
        Guiding Begins at 2026-11-01 01:30:00
        Equipment Profile = Rig A
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        2,1.000,"Mount",0.10,0.20,-0.400,-0.300,0.100,0.100,10,E,12,S,0,0,12000.0,25.0,0
        Guiding Ends at 2026-11-01 01:45:00
        Log closed at 2026-11-01 01:46:00

        """;

    /// <summary>Puts local solar noon far enough west that a 07:00 UTC start files under the
    /// previous calendar day, which is what an imaging night is for.</summary>
    private const double Longitude = -80.0;

    private static GeneralSettings Settings(
        string globalZone = "",
        double? longitude = null,
        (string Profile, string Zone)[]? profileZones = null)
    {
        var map = new Dictionary<string, Phd2ProfileEntry>(StringComparer.Ordinal);
        foreach (var (profile, zone) in profileZones ?? [])
        {
            map[profile] = new Phd2ProfileEntry { Timezone = zone };
        }

        return new GeneralSettings
        {
            ObserverTimezone = globalZone,
            ObserverLongitude = longitude,
            Phd2ProfileMap = map.Count == 0 ? null : Phd2Profiles.ToJson(map),
        };
    }

    private static Phd2Session ReadSession(TestDatabaseHandle db, Guid id)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        return context.Phd2Sessions.Single(session => session.Id == id);
    }

    private static Guid AddCalibration(GalactiLogContext context, string? profile)
    {
        var logId = Guid.NewGuid();
        context.Phd2Logs.Add(new Phd2Log
        {
            Id = logId,
            FilePath = $@"C:\library\PHD2_GuideLog_{logId:N}.txt",
            ParseStatus = "ok",
            LogVersion = "2.5",
            ParsedAt = SessionStart,
        });

        var id = Guid.NewGuid();
        context.Phd2Calibrations.Add(new Phd2Calibration
        {
            Id = id,
            LogId = logId,
            StartedAtLocal = SessionStart,
            EquipmentProfile = profile,
        });
        return id;
    }

    private static Phd2CorrelationResult RunPass(
        TestDatabaseHandle db,
        IReadOnlyCollection<DateOnly>? nights,
        IReadOnlyDictionary<string, Phd2ProfileEntry>? map = null,
        AliasMap? aliases = null)
        => Phd2Correlation.Run(
            db.ConnectionString, nights, map ?? EmptyMap, aliases ?? Aliases(), null, CancellationToken.None);

    private static AliasMap Aliases(params (string Canonical, string[] Aliases)[] telescopes)
        => new(
            new Dictionary<string, FilterSetting>(),
            new EquipmentSettings
            {
                Telescopes = telescopes.ToDictionary(
                    entry => entry.Canonical,
                    entry => new EquipmentItemSettings { Aliases = entry.Aliases }),
            });

    private static void Seed(TestDatabaseHandle db, Action<GalactiLogContext> seed)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(db.ConnectionString, tracking: true));
        seed(context);
        context.SaveChanges();
    }

    private static Image Read(TestDatabaseHandle db, Guid id)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        return context.Images.Single(image => image.Id == id);
    }

    private static Guid AddImage(
        GalactiLogContext context,
        string? telescope,
        DateTime captureUtc,
        double exposureSeconds,
        DateOnly? night = null,
        string? source = null,
        double? value = null)
    {
        var id = Guid.NewGuid();
        context.Images.Add(new Image
        {
            Id = id,
            FilePath = $@"C:\library\{id:N}.fits",
            FileName = $"{id:N}.fits",
            ImageType = "LIGHT",
            SessionDate = night ?? Night,
            CaptureDate = captureUtc,
            ExposureTime = exposureSeconds,
            Telescope = telescope,
            GuidingRmsSource = source,
            GuidingRmsArcsec = value,
            GuidingRmsRaArcsec = value,
            GuidingRmsDecArcsec = value,
        });
        return id;
    }

    /// <summary>The standard qualifying session: 40 frames at offsets 1 to 79, which is 20 samples
    /// inside a 40 second exposure opening at the session start.</summary>
    private static Guid AddGuidedSession(GalactiLogContext context, string? telescope, string? profile)
    {
        var id = AddSession(context, SessionStart, telescope, profile, PixelScale);
        AddFrames(context, id, 40);
        return id;
    }

    private static Guid AddSession(
        GalactiLogContext context,
        DateTime? startedUtc,
        string? telescope,
        string? profile,
        double? scale,
        DateOnly? night = null,
        string events = "[]",
        double durationS = 0.0,
        DateTime? startedLocal = null,
        DateTime? endedUtc = null,
        DateTime? endedLocal = null,
        bool truncated = false)
    {
        var logId = Guid.NewGuid();
        context.Phd2Logs.Add(new Phd2Log
        {
            Id = logId,
            FilePath = $@"C:\library\PHD2_GuideLog_{logId:N}.txt",
            ParseStatus = "ok",
            LogVersion = "2.5",
            ParsedAt = SessionStart,
        });

        var id = Guid.NewGuid();
        context.Phd2Sessions.Add(new Phd2Session
        {
            Id = id,
            LogId = logId,
            StartedAtLocal = startedLocal ?? startedUtc ?? SessionStart,
            StartedAtUtc = startedUtc,
            DurationS = durationS,

            Truncated = truncated,

            // The stored local end an ingest of a session this long would have written. A truncated
            // section has no Guiding Ends line at all, so it stores none whatever its duration.
            // The durationS > 0 guard is load-bearing: most call sites take the 0.0 default and
            // their rows carry a null ended_at_utc, which SessionsOverlapping reads as truncated
            // and still running. Giving those rows an end equal to their start changes what that
            // query returns and moves cases that have nothing to do with the stored local end.
            // An explicit endedLocal is AUTHORITATIVE, ahead of the truncated flag
            // (task2d-review.md P3-2). The flag used to win, so a seed asking for both lost its
            // argument with no diagnostic, and the helper could not express the shape
            // Phd2LogParser.cs:385 really produces: Truncated set by a discarded CSV row while
            // the section still carries a Guiding Ends line and therefore a stored local end.
            EndedAtLocal = endedLocal
                ?? (truncated
                    ? null
                    : durationS > 0
                        ? (startedLocal ?? startedUtc ?? SessionStart).AddSeconds(durationS)
                        : null),

            // Six hours by default, which is a realistic session length and long enough that the
            // lookback case's session still reaches the exposure it covers.
            EndedAtUtc = endedUtc ?? startedUtc?.AddHours(6),
            SessionDate = startedUtc is null ? null : night ?? Night,
            Telescope = telescope,
            EquipmentProfile = profile,
            PixelScaleArcsec = scale,
            Events = events,
        });
        return id;
    }

    /// <summary>
    /// Guide frames at <paramref name="firstOffset"/> plus a multiple of <paramref name="step"/>,
    /// alternating plus and minus one pixel in RA and two in Dec, so an even number of them has a
    /// zero mean and an exact population sigma.
    /// </summary>
    private static void AddFrames(
        GalactiLogContext context,
        Guid sessionId,
        int count,
        double firstOffset = 1.0,
        double step = 2.0,
        Func<int, (double Ra, double Dec, bool Dropped)>? shape = null)
    {
        for (var index = 0; index < count; index++)
        {
            var sign = index % 2 == 0 ? 1.0 : -1.0;
            var (ra, dec, dropped) = shape?.Invoke(index) ?? (sign, sign * 2.0, false);
            context.Phd2Frames.Add(new Phd2Frame
            {
                SessionId = sessionId,
                FrameIndex = index + 1,
                TimeOffset = firstOffset + (index * step),
                RaRaw = ra,
                DecRaw = dec,
                Dropped = dropped,
            });
        }
    }

    private static void AssertFilled(Image image)
    {
        Assert.Equal("phd2", image.GuidingRmsSource);
        Assert.Equal(ExpectedRa, image.GuidingRmsRaArcsec!.Value, 6);
        Assert.Equal(ExpectedDec, image.GuidingRmsDecArcsec!.Value, 6);
        Assert.Equal(ExpectedTotal, image.GuidingRmsArcsec!.Value, 6);
    }

    private static void AssertUnfilled(Image image)
    {
        Assert.Null(image.GuidingRmsArcsec);
        Assert.Null(image.GuidingRmsRaArcsec);
        Assert.Null(image.GuidingRmsDecArcsec);
    }
}
