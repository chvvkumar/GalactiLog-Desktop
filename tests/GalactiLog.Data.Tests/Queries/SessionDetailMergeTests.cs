using GalactiLog.Core.Aliases;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Phase 25 R3: <c>SessionDetailMerge</c> over two and three nights of one target, each night read
/// through <c>SessionDetailQuery.Get</c> exactly as the page reads it, then merged.
/// </summary>
public class SessionDetailMergeTests
{
    private static readonly DateOnly Night1 = new(2025, 3, 1);
    private static readonly DateOnly Night2 = new(2025, 3, 2);
    private static readonly DateOnly Night3 = new(2025, 3, 5);

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

        public Guid AddTarget(string primaryName) => LibrarySeeder.AddTarget(ConnectionString, primaryName).Id;

        public void AddNight(Guid targetId, DateOnly date, params Action<Image>[] frames)
        {
            foreach (var frame in frames)
            {
                LibrarySeeder.AddFrame(ConnectionString, targetId, date, image =>
                {
                    image.Camera = "Cam";
                    image.CaptureDate = date.ToDateTime(new TimeOnly(21, 0));
                    frame(image);
                });
            }
        }

        public SessionDetail Detail(Guid targetId, DateOnly date)
        {
            var detail = Query.Get(targetId.ToString(), date);
            Assert.NotNull(detail);
            return detail;
        }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    // ---- fixture helpers ---------------------------------------------------------------

    private static Action<Image> Frame(
        int minute,
        double? hfr = null,
        string filter = "L",
        double exposure = 300d,
        string telescope = "Tel",
        int? gain = null,
        double? scale = null,
        double? eccentricity = null,
        string eccentricitySource = "header",
        double? fwhm = null)
        => image =>
        {
            image.Telescope = telescope;
            image.FilterUsed = filter;
            image.CaptureDate = image.CaptureDate!.Value.AddMinutes(minute);
            image.MedianHfr = hfr;
            image.ExposureTime = exposure;
            image.CameraGain = gain;
            image.ArcsecPerPixel = scale;
            image.Eccentricity = eccentricity;
            image.EccentricitySource = eccentricity is null ? null : eccentricitySource;
            image.Fwhm = fwhm;
        };

    /// <summary>Two nights, one rig. Night 1 carries an HFR outlier (5.0 against a 2.2 median),
    /// a frame without a plate scale and a frame without a gain; night 2 is the sharper night at
    /// the other gain. Every figure a case asserts is derivable by hand from these six rows.</summary>
    private static (SessionDetail First, SessionDetail Second) TwoNights(Library library)
    {
        var target = library.AddTarget("Merge");
        library.AddNight(
            target,
            Night1,
            Frame(0, hfr: 2.0, gain: 100, scale: 1.0, eccentricity: 0.40, fwhm: 3.0),
            Frame(10, hfr: 2.2, gain: 100, scale: 1.0, eccentricity: 0.50, fwhm: 3.2),
            Frame(20, hfr: 5.0, filter: "R", exposure: 120d, eccentricity: 0.30, fwhm: 4.0));
        library.AddNight(
            target,
            Night2,
            Frame(0, hfr: 1.5, gain: 200, scale: 2.0, eccentricity: 0.45, fwhm: 2.5),
            Frame(10, hfr: 1.8, exposure: 60d, gain: 200, scale: 2.0, fwhm: 2.8),
            Frame(20, hfr: 1.6, exposure: 60d, gain: 200, scale: 2.0, eccentricity: 0.42));
        return (library.Detail(target, Night1), library.Detail(target, Night2));
    }

    private static SessionOverview Overview(
        DateOnly date,
        double integration = 300d,
        int frames = 1,
        double? hfr = null,
        double? hfrArcsec = null,
        int hfrExcluded = 0,
        double? eccentricity = null,
        string? source = "header",
        double? fwhm = null,
        double? rms = null,
        double? stars = null,
        IReadOnlyList<string>? filters = null,
        string? camera = "Cam",
        string? telescope = "Tel",
        int rigCount = 1,
        bool hasNotes = false,
        GuidingRmsProvenance provenance = GuidingRmsProvenance.None,
        int guidingSessions = 0)
        => new(
            date, integration, frames, hfr, hfrArcsec, hfrExcluded, eccentricity, source, fwhm, rms, stars,
            filters ?? ["L"], camera, telescope, rigCount, hasNotes, provenance, guidingSessions);

    // ---- frames and spans --------------------------------------------------------------

    [Fact]
    public void Merge_Frames_AreTheNightsListsConcatenatedOldestFirst_RowsReferenceEqual()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(6, merged.Frames.Count);
        for (var index = 0; index < 3; index++)
        {
            // The same row objects, so the flags and the grading are each night's own by identity.
            Assert.Same(first.Frames[index], merged.Frames[index]);
            Assert.Same(second.Frames[index], merged.Frames[index + 3]);
        }

        Assert.True(merged.Frames[2].IsHfrOutlier);
        Assert.Same(first.Frames[2].Grading, merged.Frames[2].Grading);
    }

    [Fact]
    public void Merge_Nights_CarriesOneSpanPerNightWithOffsetsAndCounts()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        Assert.Equal([new NightSpan(Night1, 0, 3)], first.Nights);
        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal([new NightSpan(Night1, 0, 3), new NightSpan(Night2, 3, 3)], merged.Nights);
    }

    [Fact]
    public void Merge_ThreeNights_ConcatenateAndSpanInOrder()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Three");
        library.AddNight(target, Night1, Frame(0, hfr: 2.0), Frame(10, hfr: 2.1));
        library.AddNight(target, Night2, Frame(0, hfr: 1.9));
        library.AddNight(target, Night3, Frame(0, hfr: 1.7), Frame(5, hfr: 1.8), Frame(10, hfr: 1.6));
        var nights = new[] { library.Detail(target, Night1), library.Detail(target, Night2), library.Detail(target, Night3) };

        var merged = SessionDetailMerge.Merge(nights);

        Assert.Equal(nights.SelectMany(night => night.Frames).Select(frame => frame.ImageId), merged.Frames.Select(frame => frame.ImageId));
        Assert.Equal([new NightSpan(Night1, 0, 2), new NightSpan(Night2, 2, 1), new NightSpan(Night3, 3, 3)], merged.Nights);
        Assert.Equal(Night1, merged.SessionDate);
        Assert.Equal(nights[0].GroupKey, merged.GroupKey);
    }

    // ---- sums, ranges and medians ------------------------------------------------------

    [Fact]
    public void Merge_FrameCountAndIntegrationSeconds_Sum()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(6, merged.FrameCount);
        Assert.Equal(1140d, merged.IntegrationSeconds);
    }

    [Fact]
    public void Merge_FiveRanges_EqualTheQueryBuilderOverTheUnion()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);
        var frames = merged.Frames;

        Assert.Equal(SessionDetailQuery.Range(frames.Select(frame => frame.MedianHfr)), merged.Hfr);
        Assert.Equal(SessionDetailQuery.Range(frames.Select(frame => frame.Fwhm)), merged.Fwhm);
        Assert.Equal(SessionDetailQuery.Range(frames.Select(frame => frame.GuidingRmsArcsec)), merged.GuidingRmsArcsec);
        Assert.Equal(SessionDetailQuery.Range(frames.Select(frame => frame.SensorTemp)), merged.SensorTemp);
        Assert.Equal(SessionDetailQuery.Range(frames.Select(frame => SessionDetailQuery.PooledEccentricity(frame, "header"))), merged.Eccentricity);

        // 1.5, 1.6, 1.8, 2.0, 2.2, 5.0; 2.5, 2.8, 3.0, 3.2, 4.0 (one frame has no FWHM).
        Assert.Equal((1.5, 5.0), (merged.Hfr.Min, merged.Hfr.Max));
        Assert.Equal(1.9, merged.Hfr.Median!.Value, 10);
        Assert.Equal((2.5, 4.0, 3.0), (merged.Fwhm.Min, merged.Fwhm.Max, merged.Fwhm.Median));
        Assert.Equal(new MetricRangeSummary(null, null, null), merged.GuidingRmsArcsec);
    }

    [Fact]
    public void Merge_MedianHfrArcsec_IsTheMedianOverPlateScaledFramesOfTheUnion()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        // 2.0, 2.2 at 1.0 arcsec per pixel and 3.0, 3.6, 3.2 at 2.0; the 5.0 frame has no scale.
        Assert.Equal(3.0, merged.MedianHfrArcsec);
        Assert.Equal(1, merged.HfrArcsecExcludedCount);
    }

    [Fact]
    public void Merge_MedianAirmassAmbientTempAndHumidity_AreOverTheUnion()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Weather");
        library.AddNight(target, Night1, image => { Frame(0)(image); image.Airmass = 1.1; image.AmbientTemp = 4.0; image.Humidity = 60; });
        library.AddNight(target, Night2, image => { Frame(0)(image); image.Airmass = 1.5; image.AmbientTemp = 8.0; image.Humidity = 80; });

        var merged = SessionDetailMerge.Merge([library.Detail(target, Night1), library.Detail(target, Night2)]);

        Assert.Equal(1.3, merged.MedianAirmass!.Value, 10);
        Assert.Equal(6.0, merged.MedianAmbientTemp);
        Assert.Equal(70d, merged.MedianHumidity);
    }

    // ---- eccentricity ------------------------------------------------------------------

    private static (SessionDetail First, SessionDetail Second) TwoSources(Library library)
    {
        var target = library.AddTarget("Sources");
        library.AddNight(
            target,
            Night1,
            Frame(0, eccentricity: 0.40),
            Frame(10, eccentricity: 0.50),
            Frame(20, eccentricity: 0.30));
        library.AddNight(
            target,
            Night2,
            Frame(0, eccentricity: 0.90, eccentricitySource: "sep"),
            Frame(10, eccentricity: 0.95, eccentricitySource: "sep"));
        return (library.Detail(target, Night1), library.Detail(target, Night2));
    }

    [Fact]
    public void Merge_Eccentricity_OneModalSourceWinsOverTheUnion()
    {
        using var library = Library.Empty();
        var (first, second) = TwoSources(library);
        Assert.Equal("sep", second.EccentricitySource);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal("header", merged.EccentricitySource);
        Assert.Equal(new MetricRangeSummary(0.30, 0.50, 0.40), merged.Eccentricity);
        Assert.Equal(0.40, Assert.Single(merged.FilterMedians).MedianEccentricity);
    }

    [Fact]
    public void Merge_Eccentricity_ExcludedCountIsRecountedOverTheUnion()
    {
        using var library = Library.Empty();
        var (first, second) = TwoSources(library);
        Assert.Equal(0, first.EccentricityExcludedCount + second.EccentricityExcludedCount);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(2, merged.EccentricityExcludedCount);
    }

    // ---- gain, exposures, times --------------------------------------------------------

    [Fact]
    public void Merge_Gain_IsTheModalGainWhenTheFirstFrameGainsDiffer()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);
        Assert.Equal(100, first.Gain);
        Assert.Equal(200, second.Gain);

        var merged = SessionDetailMerge.Merge([first, second]);

        // 100, 100 and a null on night 1 against 200, 200, 200 on night 2.
        Assert.Equal(200, merged.Gain);
    }

    [Fact]
    public void Merge_ExposureTimes_AreDistinctAndAscendingOverTheUnion()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal([60d, 120d, 300d], merged.ExposureTimes);
    }

    [Fact]
    public void Merge_FirstAndLastFrameTimes_SpanTheSet()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(Night1.ToDateTime(new TimeOnly(21, 0)), merged.FirstFrameTime);
        Assert.Equal(Night2.ToDateTime(new TimeOnly(21, 20)), merged.LastFrameTime);
    }

    // ---- per-filter rows ---------------------------------------------------------------

    [Fact]
    public void Merge_FilterMediansAndDetails_AreRebuiltOverTheUnion()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        // L was shot on both nights and yields one row, its median over all five L frames.
        Assert.Equal(["L", "R"], merged.FilterMedians.Select(row => row.FilterName));
        Assert.Equal(1.8, merged.FilterMedians[0].MedianHfr);
        Assert.All(merged.FilterMedians, row => Assert.Null(row.RigLabel));

        Assert.Equal(
            [("L", 60d, 2), ("L", 300d, 3), ("R", 120d, 1)],
            merged.FilterDetails.Select(row => (row.FilterName, row.ExposureTime!.Value, row.FrameCount)));
        Assert.Equal(
            merged.FilterDetails.Select(row => (row.FilterName, row.ExposureTime)),
            merged.FilterAcquisitions!.Select(row => (row.FilterName, row.ExposureTime)));
    }

    // ---- rigs --------------------------------------------------------------------------

    private static (SessionDetail First, SessionDetail Second) TwoRigs(Library library)
    {
        var target = library.AddTarget("Rigs");
        library.AddNight(
            target,
            Night1,
            Frame(0, hfr: 3.0, telescope: "Alpha"),
            Frame(10, hfr: 2.5, telescope: "Alpha"));
        library.AddNight(
            target,
            Night2,
            Frame(0, hfr: 2.0, telescope: "Alpha"),
            Frame(5, hfr: 1.0, telescope: "Bravo"));
        return (library.Detail(target, Night1), library.Detail(target, Night2));
    }

    [Fact]
    public void Merge_MultiRig_StampsRigLabelsOnThePerFilterRows()
    {
        using var library = Library.Empty();
        var (first, second) = TwoRigs(library);
        Assert.All(first.FilterDetails, row => Assert.Null(row.RigLabel));

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(["Alpha / Cam", "Bravo / Cam"], merged.FilterDetails.Select(row => row.RigLabel));
        Assert.Equal(["Alpha / Cam", "Bravo / Cam"], merged.FilterMedians.Select(row => row.RigLabel));
        Assert.Equal(["Alpha / Cam", "Bravo / Cam"], merged.FilterAcquisitions!.Select(row => row.RigLabel));
    }

    [Fact]
    public void Merge_Rigs_MergeByLabelWithSummedCounts()
    {
        using var library = Library.Empty();
        var (first, second) = TwoRigs(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(["Alpha / Cam", "Bravo / Cam"], merged.Rigs!.Select(rig => rig.Label));
        Assert.Equal([0, 1], merged.Rigs!.Select(rig => rig.Index));
        Assert.Equal(3, merged.Rigs![0].FrameCount);
        Assert.Equal(900d, merged.Rigs![0].IntegrationSeconds);
        Assert.Equal("Alpha", merged.Rigs![0].Telescope);
        Assert.Equal("Cam", merged.Rigs![0].Camera);
        Assert.All(merged.Rigs!, rig => Assert.NotNull(rig.Ranges));
    }

    [Fact]
    public void Merge_Rigs_ReferenceIsRankedByLowestHfrAcrossBothNights()
    {
        using var library = Library.Empty();
        var (first, second) = TwoRigs(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        Assert.Equal(second.Frames[0].ImageId, merged.Rigs![0].ReferenceImageId);
        Assert.Equal(second.Frames[0].FilePath, merged.Rigs![0].ReferenceFramePath);
        Assert.Equal(
            [second.Frames[0].FilePath, first.Frames[1].FilePath, first.Frames[0].FilePath],
            merged.Rigs![0].ReferenceCandidates);
        Assert.Equal(second.Frames[1].ImageId, merged.ReferenceImageId);
    }

    [Fact]
    public void Merge_SingleRig_LeavesRigRangesNull()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);

        var merged = SessionDetailMerge.Merge([first, second]);

        var rig = Assert.Single(merged.Rigs!);
        Assert.Null(rig.Ranges);
        Assert.Equal(6, rig.FrameCount);
    }

    // ---- insights and notes ------------------------------------------------------------

    [Fact]
    public void Merge_Insights_ArePrefixedWithTheirNightsDate()
    {
        using var library = Library.Empty();
        var (first, second) = TwoNights(library);
        var insight = Assert.Single(first.Insights);
        Assert.Empty(second.Insights);

        var merged = SessionDetailMerge.Merge([first, second]);

        var prefixed = Assert.Single(merged.Insights);
        Assert.Equal("2025-03-01: " + insight.Message, prefixed.Message);
        Assert.Equal(insight.Kind, prefixed.Kind);
        Assert.Equal(insight.Level, prefixed.Level);
        Assert.Equal("2025-03-05: ", SessionDetailMerge.InsightPrefix(Night3));
    }

    [Fact]
    public void Merge_Notes_IsNullEvenWhenAMemberHasNotes()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Notes");
        library.AddNight(target, Night1, Frame(0));
        library.AddNight(target, Night2, Frame(0));
        LibrarySeeder.AddSessionNote(library.ConnectionString, target, Night1, "clear and still");
        var first = library.Detail(target, Night1);
        Assert.Equal("clear and still", first.Notes);

        var merged = SessionDetailMerge.Merge([first, library.Detail(target, Night2)]);

        Assert.Null(merged.Notes);
    }

    // ---- the list of one ---------------------------------------------------------------

    [Fact]
    public void Merge_OfOne_ReturnsTheInput()
    {
        using var library = Library.Empty();
        var (first, _) = TwoNights(library);

        Assert.Same(first, SessionDetailMerge.Merge([first]));
        Assert.Throws<ArgumentException>(() => SessionDetailMerge.Merge([]));
    }

    // ---- the overview ------------------------------------------------------------------

    [Fact]
    public void Overview_SumsCountsAndTakesTheOldestDate()
    {
        var merged = SessionDetailMerge.Overview(
        [
            Overview(Night2, integration: 300d, frames: 1, hfrExcluded: 0, hasNotes: true),
            Overview(Night1, integration: 600d, frames: 2, hfrExcluded: 1, guidingSessions: 1),
        ]);

        Assert.Equal(Night1, merged.SessionDate);
        Assert.Equal(3, merged.FrameCount);
        Assert.Equal(900d, merged.IntegrationSeconds);
        Assert.Equal(1, merged.HfrArcsecExcludedCount);
        Assert.Equal(1, merged.GuidingSessionCount);
        Assert.True(merged.HasNotes);
    }

    [Fact]
    public void Overview_MediansAreTheMediansOfTheMembersMediansSkippingNulls()
    {
        var merged = SessionDetailMerge.Overview(
        [
            Overview(Night2, hfr: 1.0, hfrArcsec: null, eccentricity: null, fwhm: 2.0, rms: null, stars: 300),
            Overview(Night1, hfr: 2.0, hfrArcsec: 2.0, eccentricity: 0.4, fwhm: 3.0, rms: 0.5, stars: 100),
            Overview(Night3, hfr: 3.0, hfrArcsec: 4.0, eccentricity: 0.6, fwhm: null, rms: 0.7, stars: 200),
        ]);

        Assert.Equal(2.0, merged.MedianHfr);
        Assert.Equal(3.0, merged.MedianHfrArcsec);
        Assert.Equal(0.5, merged.MedianEccentricity!.Value, 10);
        Assert.Equal(2.5, merged.MedianFwhm);
        Assert.Equal(0.6, merged.MedianGuidingRmsArcsec!.Value, 10);
        Assert.Equal(200d, merged.MedianDetectedStars);
    }

    [Fact]
    public void Overview_FiltersUsed_IsTheUnionInFirstAppearanceOrderOldestNightFirst()
    {
        var merged = SessionDetailMerge.Overview(
        [
            Overview(Night2, filters: ["Ha", "L"]),
            Overview(Night1, filters: ["L", "R"]),
        ]);

        Assert.Equal(["L", "R", "Ha"], merged.FiltersUsed);
    }

    [Fact]
    public void Overview_AgreeingMembers_KeepCameraTelescopeAndSource()
    {
        var merged = SessionDetailMerge.Overview([Overview(Night1), Overview(Night2)]);

        Assert.Equal("Cam", merged.Camera);
        Assert.Equal("Tel", merged.Telescope);
        Assert.Equal("header", merged.EccentricitySource);
    }

    [Fact]
    public void Overview_DisagreeingMembers_NullCameraTelescopeAndSource()
    {
        var merged = SessionDetailMerge.Overview(
        [
            Overview(Night1),
            Overview(Night2, camera: "Other", telescope: "Big", source: "sep"),
        ]);

        Assert.Null(merged.Telescope);
        Assert.Null(merged.Camera);
        Assert.Null(merged.EccentricitySource);
    }

    [Fact]
    public void Overview_RigCount_IsAtLeastTheLargestMemberRigCount()
    {
        var merged = SessionDetailMerge.Overview(
        [
            Overview(Night1, rigCount: 1),
            Overview(Night2, camera: "Other", rigCount: 3),
        ]);

        Assert.Equal(3, merged.RigCount);
        Assert.Equal(2, SessionDetailMerge.Overview([Overview(Night1), Overview(Night2, camera: "Other")]).RigCount);
    }

    [Fact]
    public void Overview_GuidingProvenance_IsNoneOneValueOrMixed()
    {
        Assert.Equal(
            GuidingRmsProvenance.None,
            SessionDetailMerge.Overview([Overview(Night1), Overview(Night2)]).GuidingProvenance);
        Assert.Equal(
            GuidingRmsProvenance.Phd2,
            SessionDetailMerge.Overview(
            [
                Overview(Night1, provenance: GuidingRmsProvenance.Phd2),
                Overview(Night2),
                Overview(Night3, provenance: GuidingRmsProvenance.Phd2),
            ]).GuidingProvenance);
        Assert.Equal(
            GuidingRmsProvenance.Mixed,
            SessionDetailMerge.Overview(
            [
                Overview(Night1, provenance: GuidingRmsProvenance.Phd2),
                Overview(Night2, provenance: GuidingRmsProvenance.Csv),
            ]).GuidingProvenance);
    }
}
