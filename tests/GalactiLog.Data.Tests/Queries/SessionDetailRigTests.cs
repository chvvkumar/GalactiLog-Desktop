using GalactiLog.Core.Aliases;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Spec 12.4's "A multi-rig night" (PAR-004) and its per-night reference thumbnails (PAR-008) as
/// <c>SessionDetailQuery</c> returns them: the rig groups in first-capture order, the per-rig split
/// of the two per-filter lists, and the ranked reference pick.
/// </summary>
/// <remarks>
/// A file of its own rather than more cases in <see cref="SessionDetailQueryTests"/>, which pins
/// the flags, the ranges and the insight prose this phase mostly leaves alone.
/// <para>
/// The fixtures are synthetic and not <c>LibrarySeeder</c>'s. The seeder does seed two rigs, but it
/// never puts both on one night: its own comment says "the two rigs never share a night", and a rig
/// group is a property of a night. Every multi-rig case here therefore builds its own frames, in
/// the shape <c>SessionDetailGradingTests</c> established.
/// </para>
/// </remarks>
public class SessionDetailRigTests
{
    private static readonly DateOnly Day = new(2025, 4, 2);

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        private Library(TestDatabaseHandle db)
        {
            _db = db;
            var settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
            _aliases = new AliasMapCache(settings);
            Query = new SessionDetailQuery(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases,
                new RigBaselinesCache(new DatabaseConnectionString(db.ConnectionString), _aliases));
        }

        public string ConnectionString => _db.ConnectionString;

        public SessionDetailQuery Query { get; }

        public static Library Empty() => new(TestDatabaseFactory.CreateMigratedDatabase());

        public Guid AddGroup(string primaryName, params Action<Image>[] frames)
        {
            var target = LibrarySeeder.AddTarget(ConnectionString, primaryName);
            foreach (var frame in frames)
            {
                LibrarySeeder.AddFrame(ConnectionString, target.Id, Day, frame);
            }

            return target.Id;
        }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    // ---- fixture helpers ---------------------------------------------------------------
    //
    // A frame is described by its rig, its filter, its capture minute and its HFR, because those
    // four are the only inputs every rule in this file reads. The capture minute is what decides
    // the rig order and breaks a tie in the reference pick, so it is always written out.

    private const string RigA = "Alpha";
    private const string RigB = "Bravo";

    private static Action<Image> Frame(
        string telescope,
        int minute,
        double? hfr = null,
        string filter = "L",
        double exposure = 300d,
        string? name = null)
        => image =>
        {
            image.Telescope = telescope;
            image.Camera = "Cam";
            image.FilterUsed = filter;
            image.CaptureDate = Day.ToDateTime(new TimeOnly(21, 0)).AddMinutes(minute);
            image.MedianHfr = hfr;
            image.ExposureTime = exposure;
            if (name is not null)
            {
                image.FileName = name + ".fits";
                image.FilePath = @"C:\Fixture\" + name + ".fits";
            }
        };

    private static Action<Image> Undated(string telescope, double? hfr = null)
        => image =>
        {
            image.Telescope = telescope;
            image.Camera = "Cam";
            image.FilterUsed = "L";
            image.CaptureDate = null;
            image.MedianHfr = hfr;
        };

    private static SessionDetail Detail(Library library, Guid targetId)
    {
        var detail = library.Query.Get(targetId.ToString(), Day);
        Assert.NotNull(detail);
        return detail;
    }

    private static string Label(string telescope) => $"{telescope} / Cam";

    // ---- the rig groups ----------------------------------------------------------------

    [Fact]
    public void Get_ASingleRigNight_ReturnsOneRigGroup()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("OneRig", Frame(RigA, 0, 2.0d), Frame(RigA, 10, 2.5d));

        var detail = Detail(library, target);

        var rig = Assert.Single(detail.Rigs!);
        Assert.Equal(0, rig.Index);
        Assert.Equal(Label(RigA), rig.Label);
        Assert.Equal(2, rig.FrameCount);
    }

    [Fact]
    public void Get_ATwoRigNight_ReturnsTwoGroupsInFirstCaptureOrder()
    {
        using var library = Library.Empty();

        // Bravo starts first and Alpha second, so a first-capture order and an alphabetical one
        // disagree. That disagreement is the whole point of the case.
        var target = library.AddGroup(
            "TwoRigs",
            Frame(RigB, 0, 2.0d),
            Frame(RigA, 5, 2.1d),
            Frame(RigB, 10, 2.2d),
            Frame(RigA, 15, 2.3d));

        var detail = Detail(library, target);

        Assert.Equal(2, detail.Rigs!.Count);
        Assert.Equal(Label(RigB), detail.Rigs[0].Label);
        Assert.Equal(Label(RigA), detail.Rigs[1].Label);
        Assert.Equal([0, 1], detail.Rigs.Select(rig => rig.Index));
    }

    [Fact]
    public void Get_RigLabels_AreTheCanonicalPair()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Canonical", Frame(RigA, 0, 2.0d));

        var detail = Detail(library, target);

        var rig = Assert.Single(detail.Rigs!);

        // One spelling, and it is the one the frame rows carry: the insight prefixes, the pills
        // and the label rows all read this string.
        Assert.Equal("Alpha / Cam", rig.Label);
        Assert.Equal("Alpha", rig.Telescope);
        Assert.Equal("Cam", rig.Camera);
        Assert.All(detail.Frames, frame => Assert.Equal(rig.Label, frame.Rig));
    }

    [Fact]
    public void Get_RigFrameCountsAndIntegration_AreOverThatRigsOwnFrames()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "PerRigTotals",
            Frame(RigA, 0, 2.0d, exposure: 300d),
            Frame(RigA, 5, 2.1d, exposure: 300d),
            Frame(RigA, 10, 2.2d, exposure: 300d),
            Frame(RigB, 15, 2.3d, exposure: 120d));

        var detail = Detail(library, target);

        Assert.Equal(3, detail.Rigs![0].FrameCount);
        Assert.Equal(900d, detail.Rigs[0].IntegrationSeconds);
        Assert.Equal(1, detail.Rigs[1].FrameCount);
        Assert.Equal(120d, detail.Rigs[1].IntegrationSeconds);

        // And the two still add up to the night's own totals, which no figure on this page pools
        // silently.
        Assert.Equal(detail.FrameCount, detail.Rigs.Sum(rig => rig.FrameCount));
        Assert.Equal(detail.IntegrationSeconds, detail.Rigs.Sum(rig => rig.IntegrationSeconds));
    }

    // ---- the per-filter split (ruling C4) -----------------------------------------------

    [Fact]
    public void Get_ASingleRigNight_LeavesEveryRigLabelNull()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "SingleRigLabels",
            Frame(RigA, 0, 2.0d, filter: "L"),
            Frame(RigA, 5, 2.1d, filter: "R"));

        var detail = Detail(library, target);

        // That null is what makes "a single-rig night renders exactly as it does today" a property
        // of the data rather than a branch in the view.
        Assert.All(detail.FilterMedians, medians => Assert.Null(medians.RigLabel));
        Assert.All(detail.FilterDetails, row => Assert.Null(row.RigLabel));
        Assert.Equal(2, detail.FilterMedians.Count);
    }

    [Fact]
    public void Get_ATwoRigNight_SplitsTheFilterMediansPerRig()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "SplitMedians",
            Frame(RigA, 0, 2.0d, filter: "L"),
            Frame(RigA, 5, 3.0d, filter: "R"),
            Frame(RigB, 10, 4.0d, filter: "L"));

        var detail = Detail(library, target);

        Assert.Equal(3, detail.FilterMedians.Count);
        Assert.Equal(
            [Label(RigA), Label(RigA), Label(RigB)],
            detail.FilterMedians.Select(medians => medians.RigLabel));
        Assert.Equal(["L", "R", "L"], detail.FilterMedians.Select(medians => medians.FilterName));

        // Each figure is over that rig's own frames: rig B's L median is its own 4.0 and not the
        // night's pooled 3.0.
        Assert.Equal(4.0d, detail.FilterMedians[2].MedianHfr);
    }

    [Fact]
    public void Get_ATwoRigNight_SplitsTheFilterDetailsPerRig()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "SplitDetails",
            Frame(RigA, 0, 2.0d, filter: "L", exposure: 300d),
            Frame(RigB, 5, 4.0d, filter: "L", exposure: 120d),
            Frame(RigB, 10, 4.2d, filter: "L", exposure: 120d));

        var detail = Detail(library, target);

        Assert.Equal(2, detail.FilterDetails.Count);
        Assert.Equal(Label(RigA), detail.FilterDetails[0].RigLabel);
        Assert.Equal(1, detail.FilterDetails[0].FrameCount);
        Assert.Equal(Label(RigB), detail.FilterDetails[1].RigLabel);
        Assert.Equal(2, detail.FilterDetails[1].FrameCount);
        Assert.Equal(240d, detail.FilterDetails[1].IntegrationSeconds);
    }

    [Fact]
    public void Get_TheEccentricitySource_IsStillTheSessionsOwn()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "OneSource",
            Ecc(Frame(RigA, 0, 2.0d), 0.30d, "header"),
            Ecc(Frame(RigA, 5, 2.1d), 0.32d, "header"),
            Ecc(Frame(RigB, 10, 4.0d), 0.90d, "csv"));

        var detail = Detail(library, target);

        // Spec 7.2: one card, one source. The pooling rule is the session's and is not re-derived
        // per rig, or one night's two blocks would report two scales.
        Assert.Equal("header", detail.EccentricitySource);
        Assert.Equal(1, detail.EccentricityExcludedCount);

        // Rig B's only eccentricity came from the non-modal source, so its own block reports none
        // rather than reporting a figure on a second scale.
        var rigB = detail.FilterMedians.Single(medians => medians.RigLabel == Label(RigB));
        Assert.Null(rigB.MedianEccentricity);
    }

    private static Action<Image> Ecc(Action<Image> frame, double eccentricity, string source)
        => image =>
        {
            frame(image);
            image.Eccentricity = eccentricity;
            image.EccentricitySource = source;
        };

    // ---- the insight order (questions.md Q10) -------------------------------------------

    [Fact]
    public void Get_ThePerRigInsights_FollowTheSameRigOrder()
    {
        using var library = Library.Empty();

        // Bravo first by capture time, Alpha second, and both carry an HFR outlier so both emit a
        // prefixed sentence. Alphabetically Alpha would come first, which is the order this phase
        // replaced.
        List<Action<Image>> frames =
        [
            .. Enumerable.Range(0, 4).Select(i => Frame(RigB, i, 2.0d)),
            Frame(RigB, 4, 20.0d),
            .. Enumerable.Range(0, 4).Select(i => Frame(RigA, 10 + i, 2.0d)),
            Frame(RigA, 14, 20.0d),
        ];
        var target = library.AddGroup("InsightOrder", [.. frames]);

        var detail = Detail(library, target);

        var prefixed = detail.Insights
            .Where(insight => insight.Message.StartsWith('['))
            .Select(insight => insight.Message)
            .ToList();

        Assert.Equal(2, prefixed.Count);
        Assert.StartsWith($"[{Label(RigB)}]", prefixed[0]);
        Assert.StartsWith($"[{Label(RigA)}]", prefixed[1]);

        // And the insight order is the rig list's order, not a second order of its own.
        Assert.Equal(Label(RigB), detail.Rigs![0].Label);
    }

    // ---- the reference pick (PAR-008) ---------------------------------------------------

    [Fact]
    public void Get_TheReferencePick_IsTheLowestNonZeroMedianHfr()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Sharpest",
            Frame(RigA, 0, 3.0d, name: "blunt"),
            Frame(RigA, 5, 1.8d, name: "sharpest"),
            Frame(RigA, 10, 2.4d, name: "middling"));

        var detail = Detail(library, target);

        Assert.EndsWith("sharpest.fits", detail.ReferenceFramePath);
        Assert.Equal(
            detail.Frames.Single(frame => frame.MedianHfr == 1.8d).ImageId,
            detail.ReferenceImageId);
    }

    [Fact]
    public void Get_TheReferencePick_TieGoesToTheNewerFrame()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Tie",
            Frame(RigA, 0, 1.8d, name: "earlier"),
            Frame(RigA, 30, 1.8d, name: "later"),
            Frame(RigA, 40, 2.4d, name: "blunt"));

        var detail = Detail(library, target);

        Assert.EndsWith("later.fits", detail.ReferenceFramePath);
    }

    [Fact]
    public void Get_AZeroMedianHfr_IsNotACandidate()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "ZeroHfr",
            Frame(RigA, 0, 0d, name: "zero"),
            Frame(RigA, 5, 2.4d, name: "measured"));

        var detail = Detail(library, target);

        // A stored zero is not a measurement, which is the same rule the web's select_best_frame
        // applies.
        Assert.EndsWith("measured.fits", detail.ReferenceFramePath);
        Assert.DoesNotContain(detail.ReferenceCandidates!, path => path.EndsWith("zero.fits"));
    }

    [Fact]
    public void Get_ANightWithNoMedianHfr_FallsBackToTheNewestDatedFrame()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "NoHfr",
            Frame(RigA, 0, name: "oldest"),
            Frame(RigA, 30, name: "newest"));

        var detail = Detail(library, target);

        // Section 11.4's rule, which is the case a night scanned before any metric was derived
        // lands in.
        Assert.EndsWith("newest.fits", detail.ReferenceFramePath);
    }

    [Fact]
    public void Get_ANightWithNoDatedFrame_HasNoReference()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("NoDate", Undated(RigA), Undated(RigA));

        var detail = Detail(library, target);

        Assert.Null(detail.ReferenceImageId);
        Assert.Null(detail.ReferenceFramePath);
        Assert.Empty(detail.ReferenceCandidates!);
        Assert.Empty(detail.Rigs![0].ReferenceCandidates!);
    }

    [Fact]
    public void Get_ATwoRigNight_PicksOneReferencePerRig()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "PerRigReference",
            Frame(RigA, 0, 2.6d, name: "alpha-blunt"),
            Frame(RigA, 5, 2.2d, name: "alpha-sharp"),
            Frame(RigB, 10, 1.4d, name: "bravo-sharp"),
            Frame(RigB, 15, 3.9d, name: "bravo-blunt"));

        var detail = Detail(library, target);

        Assert.EndsWith("alpha-sharp.fits", detail.Rigs![0].ReferenceFramePath);
        Assert.EndsWith("bravo-sharp.fits", detail.Rigs[1].ReferenceFramePath);

        // The night's own pick is over every frame, so it is the sharpest of the two rigs' best.
        Assert.EndsWith("bravo-sharp.fits", detail.ReferenceFramePath);
    }

    [Fact]
    public void Get_TheCandidateList_IsAtMostThreeInRankedOrder()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Candidates",
            Frame(RigA, 0, 5.0d, name: "fifth"),
            Frame(RigA, 5, 1.1d, name: "first"),
            Frame(RigA, 10, 4.0d, name: "fourth"),
            Frame(RigA, 15, 2.2d, name: "second"),
            Frame(RigA, 20, 3.3d, name: "third"));

        var detail = Detail(library, target);

        // Spec 12.4's decode walk tries at most three, so ranking more would be work nothing reads.
        Assert.Equal(3, detail.ReferenceCandidates!.Count);
        Assert.EndsWith("first.fits", detail.ReferenceCandidates[0]);
        Assert.EndsWith("second.fits", detail.ReferenceCandidates[1]);
        Assert.EndsWith("third.fits", detail.ReferenceCandidates[2]);
        Assert.Equal(detail.ReferenceCandidates[0], detail.ReferenceFramePath);
    }

    // ---- the single-rig night is unchanged ----------------------------------------------

    [Fact]
    public void Get_ASingleRigNight_CarriesNoPerRigRanges()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("NoRigRanges", Frame(RigA, 0, 2.0d), Frame(RigA, 5, 2.4d));

        var detail = Detail(library, target);

        // The ranges table on a single-rig night is the session's own and is exactly what it was,
        // which is what leaving the rig's list null expresses.
        Assert.Null(Assert.Single(detail.Rigs!).Ranges);
    }

    [Fact]
    public void Get_ATwoRigNight_CarriesFiveRangesPerRig()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "RigRanges",
            Frame(RigA, 0, 2.0d),
            Frame(RigA, 5, 3.0d),
            Frame(RigB, 10, 6.0d),
            Frame(RigB, 15, 8.0d));

        var detail = Detail(library, target);

        Assert.Equal(5, detail.Rigs![0].Ranges!.Count);
        Assert.Equal(2.0d, detail.Rigs[0].Ranges![0].Min);
        Assert.Equal(3.0d, detail.Rigs[0].Ranges![0].Max);
        Assert.Equal(6.0d, detail.Rigs[1].Ranges![0].Min);
        Assert.Equal(8.0d, detail.Rigs[1].Ranges![0].Max);

        // The night's own range still spans both rigs: no figure on a multi-rig night pools two
        // rigs silently, and this one says so by naming the night rather than a rig.
        Assert.Equal(2.0d, detail.Hfr.Min);
        Assert.Equal(8.0d, detail.Hfr.Max);
    }
}
