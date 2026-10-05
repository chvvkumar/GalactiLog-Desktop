using System.Text.RegularExpressions;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Spec 12.4's per-frame quality grading as <c>SessionDetailQuery</c> returns it: nine
/// <see cref="MetricGrade"/>s per frame, both baselines at once, computed from the frames the one
/// session query already read. A file of its own rather than 52 more cases in
/// <see cref="SessionDetailQueryTests"/>, which pins the flags and the insights this phase does
/// not touch.
/// </summary>
public class SessionDetailGradingTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day = new(2025, 3, 1);
    private static readonly DateOnly History = new(2024, 11, 4);

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        private Library(TestDatabaseHandle db)
        {
            _db = db;
            Settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
            _aliases = new AliasMapCache(Settings);
            Baselines = new RigBaselinesCache(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases);
            Query = new SessionDetailQuery(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases,
                Baselines);
        }

        public string ConnectionString => _db.ConnectionString;

        public SettingsStore Settings { get; }

        public RigBaselinesCache Baselines { get; }

        public SessionDetailQuery Query { get; }

        public static Library Empty() => new(TestDatabaseFactory.CreateMigratedDatabase());

        public static Library Seeded()
        {
            var library = Empty();
            LibrarySeeder.Seed(library.ConnectionString);
            return library;
        }

        public Target AddTarget(string primaryName)
            => LibrarySeeder.AddTarget(ConnectionString, primaryName);

        public Image AddFrame(Guid? targetId, DateOnly sessionDate, Action<Image>? configure = null)
            => LibrarySeeder.AddFrame(ConnectionString, targetId, sessionDate, configure);

        public Guid AddGroup(string primaryName, params Action<Image>[] frames)
        {
            var target = AddTarget(primaryName);
            foreach (var frame in frames)
            {
                AddFrame(target.Id, Day, image =>
                {
                    Rig(image);
                    frame(image);
                });
            }

            return target.Id;
        }

        /// <summary>Library history under its own target on another date, so it widens the rig
        /// baseline without joining the session under test.</summary>
        public void AddHistory(string primaryName, params Action<Image>[] frames)
        {
            var target = AddTarget(primaryName);
            foreach (var frame in frames)
            {
                AddFrame(target.Id, History, image =>
                {
                    Rig(image);
                    frame(image);
                });
            }
        }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    private static void Rig(Image image)
    {
        image.Telescope = "Tel";
        image.Camera = "Cam";
        image.FilterUsed = "L";
    }

    private static Action<Image> Hfr(double hfr) => image => image.MedianHfr = hfr;

    private static Action<Image> Ecc(double eccentricity, string source = "header")
        => image =>
        {
            image.Eccentricity = eccentricity;
            image.EccentricitySource = source;
        };

    private static Action<Image> Stars(double detected) => image => image.DetectedStars = (int)detected;

    private static SessionDetail Detail(Library library, Guid targetId, DateOnly? date = null)
    {
        var detail = library.Query.Get(targetId.ToString(), date ?? Day);
        Assert.NotNull(detail);
        return detail;
    }

    /// <summary>Eight frames at 2.0 and 3.0 plus one at <paramref name="outlier"/>. With
    /// <paramref name="outlier"/> above 3.0 the nine sorted values put the median at 3.0 and the
    /// MAD at 1.0, and N is 9, so the outlier's deviation is exactly <c>outlier - 3.0</c>. Every
    /// value is exact in binary, so a boundary assertion is not a rounding accident.</summary>
    private static IEnumerable<Action<Image>> HfrSpread(double outlier)
        => [.. Enumerable.Range(0, 8).Select(i => Hfr(i < 4 ? 2.0d : 3.0d)), Hfr(outlier)];

    // ---- every row carries one ---------------------------------------------------------

    [Fact]
    public void Get_EveryFrameRow_CarriesAGrading()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Graded", [.. HfrSpread(5.0d)]);

        var detail = Detail(library, target);

        Assert.All(detail.Frames, frame => Assert.NotNull(frame.Grading));
    }

    [Fact]
    public void Get_TheSessionDeviations_MatchFrameQualityOverTheNightsOwnFrames()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("SessionGraded", [.. HfrSpread(5.0d)]);

        var detail = Detail(library, target);

        // Median 3.0, MAD 1.0, so the 5.0 frame sits two MAD units above the group.
        var outlier = Assert.Single(detail.Frames, frame => frame.MedianHfr == 5.0d);
        Assert.Equal(2.0d, outlier.Grading!.SessionHfr.Z!.Value, 9);
        Assert.Equal(3.0d, outlier.Grading.SessionHfr.BaselineMedian!.Value, 9);

        // And the same numbers computed straight from FrameQuality over the same frames, so the
        // query cannot drift from the math the insights and the Statistics MADs read.
        var graded = detail.Frames
            .Select(frame => new GradedFrame(
                "Tel", "Cam", "L", frame.MedianHfr, frame.Fwhm, frame.Eccentricity,
                frame.DetectedStars, frame.AduMedian, frame.GuidingRmsArcsec))
            .ToList();
        var baselines = FrameQuality.GroupBaselines(graded);
        var baseline = baselines[FrameQuality.GroupKey(graded[0])]["median_hfr"];

        foreach (var frame in detail.Frames)
        {
            Assert.Equal(
                FrameQuality.MadZ(frame.MedianHfr, baseline),
                frame.Grading!.SessionHfr.Z);
        }
    }

    [Fact]
    public void Get_TheRigDeviations_MatchTheRigBaselines()
    {
        using var library = Library.Empty();

        // The library's own history for this rig and filter: ten frames at 6.0 and 8.0, well away
        // from the night's own 2.0 to 3.0 spread, so the two baselines are genuinely different
        // scales and a frame reads differently under each.
        library.AddHistory("History", [.. Enumerable.Range(0, 10).Select(i => Hfr(i < 5 ? 6.0d : 8.0d))]);
        var target = library.AddGroup("RigGraded", [.. HfrSpread(5.0d)]);

        var detail = Detail(library, target);
        var rigGroups = library.Baselines.Current.Groups;
        var groupKey = FrameQuality.GroupKey(
            new GradedFrame("Tel", "Cam", "L", null, null, null, null, null, null));
        var rigBaseline = rigGroups[groupKey]["median_hfr"];

        foreach (var frame in detail.Frames)
        {
            Assert.Equal(
                FrameQuality.MadZ(frame.MedianHfr, rigBaseline),
                frame.Grading!.RigHfr.Z);
        }

        // The two baselines are genuinely different, which is the whole point of the toggle: the
        // night's own frames put the 5.0 frame two MAD units above its group, and the library's
        // wider history puts the same frame below its own median.
        var outlier = Assert.Single(detail.Frames, frame => frame.MedianHfr == 5.0d);
        Assert.Equal(2.0d, outlier.Grading!.SessionHfr.Z!.Value, 9);
        Assert.True(
            outlier.Grading.RigHfr.Z!.Value < 0d,
            $"the rig deviation is {outlier.Grading.RigHfr.Z.Value}, which is not on the better side");
        Assert.NotEqual(
            outlier.Grading.SessionHfr.BaselineMedian!.Value,
            outlier.Grading.RigHfr.BaselineMedian!.Value,
            6);
    }

    [Fact]
    public void Get_ASparseGroup_GradesToNull()
    {
        // Seven frames is below FrameQuality.MinGroup, so nothing in the night is graded at all.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Sparse",
            [.. Enumerable.Range(0, 7).Select(i => Hfr(2.0d + i))]);

        var detail = Detail(library, target);

        Assert.Equal(7, detail.Frames.Count);
        Assert.All(detail.Frames, frame =>
        {
            Assert.NotNull(frame.Grading);
            Assert.Null(frame.Grading!.SessionHfr.Z);
        });
    }

    [Fact]
    public void Get_AUniformGroup_GradesToNull()
    {
        // Ten frames at one value: N is above MinGroup but the MAD is 0, so the group is uniform
        // and carries no deviation. The median is still reported, because it exists.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Uniform",
            [.. Enumerable.Range(0, 10).Select(_ => Hfr(2.5d))]);

        var detail = Detail(library, target);

        Assert.All(detail.Frames, frame =>
        {
            Assert.Null(frame.Grading!.SessionHfr.Z);
            Assert.Equal(2.5d, frame.Grading.SessionHfr.BaselineMedian!.Value, 9);
        });
    }

    [Fact]
    public void Get_DetectedStars_IsGradedHigherIsBetter()
    {
        using var library = Library.Empty();

        // Eight frames at 100 and 200 plus one at 50. The nine sorted values put the median at 100
        // and the MAD at 50. Fewer stars is a worse frame, so the low frame's raw deviation of -1
        // comes back as +1 after the sign flip.
        var target = library.AddGroup(
            "Stars",
            [.. Enumerable.Range(0, 8).Select(i => Stars(i < 4 ? 100d : 200d)), Stars(50d)]);

        var detail = Detail(library, target);
        var worst = Assert.Single(detail.Frames, frame => frame.DetectedStars == 50);

        Assert.True(worst.Grading!.DetectedStars.Z > 0d, "fewer stars than the baseline must grade as worse");
        Assert.Equal(1.0d, worst.Grading.DetectedStars.Z!.Value, 9);

        // And a frame above the baseline is better, which is the sign flip working both ways.
        var best = detail.Frames.First(frame => frame.DetectedStars == 200);
        Assert.True(best.Grading!.DetectedStars.Z < 0d);
    }

    [Fact]
    public void Get_TheThreeSignalMetrics_CarryNoRigGrade()
    {
        // Spec 12.4's Compare to block: the toggle governs the sharpness and roundness metrics
        // only. Detected stars, median ADU and guiding RMS have no rig twin on the record at all,
        // so there is nothing for a later reader to grade against the wrong baseline.
        var members = typeof(FrameGrading).GetProperties().Select(property => property.Name).ToArray();

        Assert.Contains("RigHfr", members);
        Assert.Contains("RigEccentricity", members);
        Assert.Contains("RigFwhm", members);
        Assert.DoesNotContain("RigDetectedStars", members);
        Assert.DoesNotContain("RigAduMedian", members);
        Assert.DoesNotContain("RigGuidingRms", members);

        // And the three that exist are graded, against the session, which is the Q7 departure from
        // the web: SessionAccordionCard.tsx does not grade the guiding RMS columns at all.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Signal",
            [.. Enumerable.Range(0, 10).Select(i => (Action<Image>)(image =>
            {
                image.DetectedStars = i < 5 ? 100 : 200;
                image.AduMedian = i < 5 ? 1000d : 2000d;
                image.GuidingRmsArcsec = i < 5 ? 0.4d : 0.6d;
            }))]);

        var frame = Detail(library, target).Frames[0];

        Assert.NotNull(frame.Grading!.DetectedStars.Z);
        Assert.NotNull(frame.Grading.AduMedian.Z);
        Assert.NotNull(frame.Grading.GuidingRms.Z);
    }

    [Fact]
    public void Get_ASessionPooledOnANonModalEccentricitySource_HasNoRigEccentricityGrade()
    {
        using var library = Library.Empty();

        // The library's modal eccentricity source is "header": twenty history frames carry it
        // against the night's ten on "fits", so the modal pick is a clear majority rather than a
        // tie, and the night pools on a scale the rig baseline was not built from. Spec 7.2: a
        // cross-source deviation is a wrong number, not an approximate one, which is the same
        // refusal EccentricityVsRig already makes for the whole-night sentence.
        library.AddHistory(
            "History",
            [.. Enumerable.Range(0, 20).Select(i => Ecc(i < 10 ? 0.30d : 0.40d))]);
        var target = library.AddGroup(
            "CrossSource",
            [.. Enumerable.Range(0, 10).Select(i => Ecc(i < 5 ? 0.30d : 0.40d, "fits"))]);

        var detail = Detail(library, target);

        Assert.Equal("fits", detail.EccentricitySource);
        Assert.Equal("header", library.Baselines.Current.EccentricitySource);
        Assert.All(detail.Frames, frame =>
        {
            Assert.Null(frame.Grading!.RigEccentricity.Z);
            Assert.Null(frame.Grading.RigEccentricity.BaselineMedian);

            // The session's own eccentricity grade is unaffected: one source graded against
            // itself is a comparison that holds.
            Assert.NotNull(frame.Grading.SessionEccentricity.Z);
        });
    }

    [Fact]
    public void Get_AFrameFromANonModalSource_HasNoEccentricityGradeAtAll()
    {
        using var library = Library.Empty();

        // Nine frames on the modal source and one on another. The odd frame's pooled eccentricity
        // is null (spec 7.2), so it grades to null under either baseline, which is the same rule
        // the eccentricity flag already follows. The nine are spread over three values rather than
        // two, and over values that are exact in binary, so their own MAD is not zero and they
        // really are graded: a two-value group of nine has a MAD of 0 and would be ungraded for a
        // reason that has nothing to do with the source.
        var target = library.AddGroup(
            "MixedSources",
            [
                .. Enumerable.Range(0, 9).Select(i => Ecc(0.25d + (i % 3 * 0.25d))),
                Ecc(0.90d, "fits"),
            ]);

        var detail = Detail(library, target);
        var odd = Assert.Single(detail.Frames, frame => frame.Eccentricity == 0.90d);

        Assert.Null(odd.Grading!.SessionEccentricity.Z);
        Assert.Null(odd.Grading.RigEccentricity.Z);

        // Its neighbours on the modal source are graded, so the null is the source rule and not a
        // sparse group and not a uniform one.
        var neighbour = detail.Frames.First(frame => frame.Eccentricity == 0.75d);
        Assert.NotNull(neighbour.Grading!.SessionEccentricity.Z);
    }

    // ---- the roadmap's Verify clause, on the deterministic fixture ----------------------

    [Fact]
    public void Get_OnTheSeededLibrary_TheRejectBandHoldsTheKnownOutliers()
    {
        // The fixture night the roadmap's Verify clause names: session index 0 of Targets[0], the
        // same night SessionDetailQueryTests asserts the flags on. The seeder's own outlier rules
        // are what put frames in the reject band; nothing here types a threshold the seeder does
        // not publish.
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[0];

        var detail = Detail(library, seeded.Id, seeded.FirstSession);

        // Every flagged frame's own metric is in the reject band under the session baseline: the
        // flag rule and the band's own 3.0 threshold are the same number
        // (FrameQuality.ZReject), so a frame the eccentricity rule flagged cannot be neutral.
        foreach (var frame in detail.Frames.Where(frame => frame.IsEccentricityOutlier))
        {
            Assert.Equal(
                QualityBand.Reject,
                FrameQuality.BandForZ(frame.Grading!.SessionEccentricity.Z));
        }

        // And the bands are not the flags: the reject band is a per-metric verdict and the two
        // sets are allowed to differ, which is exactly why both marks exist.
        var rejectCells = detail.Frames.Count(frame =>
            FrameQuality.BandForZ(frame.Grading!.SessionHfr.Z) == QualityBand.Reject
            || FrameQuality.BandForZ(frame.Grading.SessionEccentricity.Z) == QualityBand.Reject);
        Assert.True(rejectCells >= detail.Frames.Count(frame => frame.IsEccentricityOutlier));

        // Spec 12.4's tally over the same night, computed here from the same two Core functions the
        // frame table's own tally reads, so the figure this phase's report quotes is a measured one
        // rather than a claimed one. Every frame is in exactly one of the four buckets.
        var good = 0;
        var watch = 0;
        var reject = 0;
        var ungraded = 0;
        var total = 0d;

        foreach (var frame in detail.Frames)
        {
            var score = FrameQuality.CombinedScore(
                frame.Grading!.DetectedStars.Z,
                frame.Grading.SessionHfr.Z,
                frame.Grading.SessionEccentricity.Z);

            switch (FrameQuality.BandForScore(score))
            {
                case null:
                    ungraded++;
                    break;
                case QualityBand.Watch:
                    watch++;
                    total += score!.Value;
                    break;
                case QualityBand.Reject:
                    reject++;
                    total += score!.Value;
                    break;
                default:
                    good++;
                    total += score!.Value;
                    break;
            }
        }

        var scored = good + watch + reject;
        output.WriteLine(
            $"seeded night tally: Good {good} Watch {watch} Reject {reject} " +
            $"Ungraded {ungraded} Mean {(scored == 0 ? double.NaN : total / scored):0.0} " +
            $"over {detail.Frames.Count} frames");

        Assert.Equal(detail.Frames.Count, good + watch + reject + ungraded);
    }

    // ---- one session query (spec 12.4's queries paragraph) ------------------------------

    [Fact]
    public void Get_IssuesNoSecondQuery()
    {
        // Spec 12.4: the page issues one session query. The grading is computed from the rows that
        // query already returned, so the statement count does not move. Asserted over the source,
        // because Microsoft.Data.Sqlite exposes no per-connection command counter and the one
        // command this method builds carries its one or two statements in a single batch.
        var source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "GalactiLog.Data", "Queries", "SessionDetailQuery.cs"));

        Assert.Single(Regex.Matches(source, @"\bCreateCommand\(\)"));
        Assert.Single(Regex.Matches(source, @"\bCommandText\s*="));
        Assert.Single(Regex.Matches(source, @"\bExecuteReader\(\)"));

        // And the grading builder issues nothing of its own: it takes the already-read frames and
        // the two baseline dictionaries and returns an array.
        const string signature = "private FrameGrading[] BuildGrading(";
        const string next = "private static string Plural";

        Assert.Contains(signature, source, StringComparison.Ordinal);

        var body = source[source.IndexOf(signature, StringComparison.Ordinal)..];
        body = body[..body.IndexOf(next, StringComparison.Ordinal)];
        Assert.DoesNotContain("CreateCommand", body, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found.");
    }
}
