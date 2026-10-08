using System.Diagnostics;
using System.Globalization;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Data.Tests.Queries;

public class SessionDetailQueryTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day = new(2025, 3, 1);
    private static readonly DateOnly History = new(2024, 11, 4);

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        private Library(TestDatabaseHandle db, Func<DateTime>? utcNow)
        {
            _db = db;
            Settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
            _aliases = new AliasMapCache(Settings);
            Baselines = new RigBaselinesCache(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases,
                utcNow);
            Query = new SessionDetailQuery(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases,
                Baselines);
        }

        public string ConnectionString => _db.ConnectionString;
        public SettingsStore Settings { get; }
        public RigBaselinesCache Baselines { get; }
        public SessionDetailQuery Query { get; }

        public static Library Empty(Func<DateTime>? utcNow = null)
            => new(TestDatabaseFactory.CreateMigratedDatabase(), utcNow);

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

        /// <summary>A resolved target plus one frame per supplied shape, all on <see cref="Day"/>.
        /// Every frame gets the same rig and filter unless the shape overrides them, so a fixture
        /// that says nothing about equipment lands in one baseline group.</summary>
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

        /// <summary>Library history for the rig baseline: frames on another date under their own
        /// target, so they widen the library baseline without joining the session under test.
        /// </summary>
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

        public void AddNote(Guid targetId, DateOnly sessionDate, string notes)
        {
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(ConnectionString, tracking: true));
            context.SessionNotes.Add(new SessionNote
            {
                Id = Guid.NewGuid(),
                TargetId = targetId,
                SessionDate = sessionDate,
                Notes = notes,
                UpdatedAt = DateTime.UtcNow,
            });
            context.SaveChanges();
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

    /// <summary>Every eccentricity fixture sets a source, because spec 7.2 pools on the modal one
    /// and a fixture that left it null would be pooling on null by accident rather than by
    /// intent.</summary>
    private static Action<Image> Ecc(double eccentricity, string source = "header")
        => image =>
        {
            image.Eccentricity = eccentricity;
            image.EccentricitySource = source;
        };

    private static Action<Image> Hfr(double hfr) => image => image.MedianHfr = hfr;

    private static Action<Image> Fwhm(double fwhm) => image => image.Fwhm = fwhm;

    private static Action<Image> Stars(int detected) => image => image.DetectedStars = detected;

    private static Action<Image> Rms(double rms) => image => image.GuidingRmsArcsec = rms;

    private static SessionDetail Detail(Library library, Guid targetId, DateOnly? date = null)
    {
        var detail = library.Query.Get(targetId.ToString(), date ?? Day);
        Assert.NotNull(detail);
        return detail;
    }

    private static string Message(SessionDetail detail, string kind)
        => Assert.Single(detail.Insights, insight => insight.Kind == kind).Message;

    // ---- scope ------------------------------------------------------------------------

    [Fact]
    public void Get_ReadsOnlyTheRequestedSession()
    {
        // The laziness the roadmap's Verify line asks about is a view-model property (Task 4's
        // SessionCard_Collapsed_IssuesNoQuery). What this query can prove about itself is that
        // one card's figures come from one night: the neighbouring sessions are wildly different
        // and must move nothing.
        using var library = Library.Empty();
        var target = library.AddGroup("Scoped", Hfr(2d), Hfr(2d), Hfr(2d));
        library.AddFrame(target, Day.AddDays(-1), image =>
        {
            Rig(image);
            image.MedianHfr = 99d;
        });
        library.AddFrame(target, Day.AddDays(1), image =>
        {
            Rig(image);
            image.MedianHfr = 99d;
        });

        var detail = Detail(library, target);

        Assert.Equal(3, detail.FrameCount);
        Assert.Equal(3, detail.Frames.Count);
        Assert.Equal(2d, detail.Hfr.Max);
        Assert.Equal(3 * LibrarySeeder.ExposureSeconds, detail.IntegrationSeconds);
        Assert.Equal(Day, detail.SessionDate);
    }

    [Fact]
    public void Get_UnknownSession_ReturnsNull()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[0];

        // A real target on a night it did not image.
        Assert.Null(library.Query.Get(seeded.Id.ToString(), seeded.FirstSession.AddDays(1)));
        // A group key nothing in the library uses, on a night that does exist.
        Assert.Null(library.Query.Get(Guid.NewGuid().ToString(), seeded.FirstSession));
        Assert.Null(library.Query.Get("obj:Nothing", seeded.FirstSession));
    }

    [Fact]
    public void Get_CalibrationFrames_AreExcluded()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Calibration", Hfr(2d), Hfr(2d));
        foreach (var type in new string?[] { "DARK", "FLAT", "BIAS", null })
        {
            library.AddFrame(target, Day, image =>
            {
                Rig(image);
                image.ImageType = type;
                image.MedianHfr = 500d;
                image.ExposureTime = 999d;
            });
        }

        var detail = Detail(library, target);

        Assert.Equal(2, detail.FrameCount);
        Assert.Equal(2d, detail.Hfr.Max);
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, detail.IntegrationSeconds);
    }

    // ---- ranges -----------------------------------------------------------------------

    [Fact]
    public void Get_Ranges_ReportMinMaxAndMedianForEachMetric()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Ranges",
            Metrics(1d, 0.2d, 3d, 0.4d, -10d),
            Metrics(2d, 0.3d, 4d, 0.5d, -9d),
            Metrics(6d, 0.7d, 8d, 0.9d, -5d));

        var detail = Detail(library, target);

        AssertRange(detail.Hfr, 1d, 6d, 2d);
        AssertRange(detail.Eccentricity, 0.2d, 0.7d, 0.3d);
        AssertRange(detail.Fwhm, 3d, 8d, 4d);
        AssertRange(detail.GuidingRmsArcsec, 0.4d, 0.9d, 0.5d);
        AssertRange(detail.SensorTemp, -10d, -5d, -9d);

        static Action<Image> Metrics(double hfr, double ecc, double fwhm, double guiding, double temp)
            => image =>
            {
                image.MedianHfr = hfr;
                image.Eccentricity = ecc;
                image.EccentricitySource = "header";
                image.Fwhm = fwhm;
                image.GuidingRmsArcsec = guiding;
                image.SensorTemp = temp;
            };
    }

    [Fact]
    public void Get_Ranges_MetricAbsentFromEveryFrame_IsAllNull()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Sparse", Hfr(2d), Hfr(4d));

        var detail = Detail(library, target);

        AssertRange(detail.Hfr, 2d, 4d, 3d);
        foreach (var range in new[] { detail.Eccentricity, detail.Fwhm, detail.GuidingRmsArcsec, detail.SensorTemp })
        {
            // All three fields null, never a zero standing in for "not measured".
            Assert.Null(range.Min);
            Assert.Null(range.Max);
            Assert.Null(range.Median);
        }

        Assert.Null(detail.MedianAirmass);
        Assert.Null(detail.MedianAmbientTemp);
        Assert.Null(detail.MedianHumidity);
        Assert.Null(detail.Gain);
    }

    private static void AssertRange(MetricRangeSummary range, double min, double max, double median)
    {
        Assert.Equal(min, range.Min);
        Assert.Equal(max, range.Max);
        Assert.Equal(median, range.Median);
    }

    [Fact]
    public void Get_HfrArcsec_IsAMedianOverPlateScaledFramesOnly_WithTheExcludedCount()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Arcsec",
            Scaled(2d, 0.5d),
            Scaled(4d, 0.5d),
            Scaled(8d, 2d),
            Scaled(10d, null),
            Scaled(12d, 0d),
            image => image.MedianHfr = null);

        var detail = Detail(library, target);

        // Converted values are 1, 2 and 16: their median is 2, their mean would be 6.33.
        Assert.Equal(2d, detail.MedianHfrArcsec);
        // A null plate scale and a non-positive one are both excluded; a frame with no HFR at all
        // is in neither pool, because it was not excluded for lack of a plate scale.
        Assert.Equal(2, detail.HfrArcsecExcludedCount);

        static Action<Image> Scaled(double hfr, double? scale)
            => image =>
            {
                image.MedianHfr = hfr;
                image.ArcsecPerPixel = scale;
            };
    }

    [Fact]
    public void Get_Fwhm_ReadsTheFwhmColumnNotMedianFwhm()
    {
        // Spec 7.1.1: the header FWHM is a different number and appears only in Task 6's panel.
        using var library = Library.Empty();
        var target = library.AddGroup("Fwhm", image =>
        {
            image.MedianFwhm = 9d;
            image.Fwhm = null;
        });

        var detail = Detail(library, target);

        Assert.Null(detail.Fwhm.Min);
        Assert.Null(detail.Fwhm.Max);
        Assert.Null(detail.Fwhm.Median);
        Assert.Null(Assert.Single(detail.Frames).Fwhm);
        Assert.Null(Assert.Single(detail.FilterMedians).MedianFwhm);
    }

    [Fact]
    public void Get_ExposureTimes_AreDistinctAndAscending()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Exposures",
            Exposure(300d),
            Exposure(60d),
            Exposure(300d),
            Exposure(120d),
            Exposure(null));

        var detail = Detail(library, target);

        Assert.Equal([60d, 120d, 300d], detail.ExposureTimes);
        // A null exposure contributes nothing to the set and 0 to the integration.
        Assert.Equal(780d, detail.IntegrationSeconds);

        static Action<Image> Exposure(double? seconds) => image => image.ExposureTime = seconds;
    }

    [Fact]
    public void Get_GainAndFrameTimes_ComeFromTheCaptureOrderedFrames()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Ordered");
        // Inserted latest first, so insertion order cannot be what the query reports.
        library.AddFrame(target.Id, Day, image =>
        {
            Rig(image);
            image.CaptureDate = Day.ToDateTime(new TimeOnly(23, 30));
            image.CameraGain = 300;
        });
        library.AddFrame(target.Id, Day, image =>
        {
            Rig(image);
            image.CaptureDate = Day.ToDateTime(new TimeOnly(21, 0));
            image.CameraGain = 100;
        });
        library.AddFrame(target.Id, Day, image =>
        {
            Rig(image);
            image.CaptureDate = Day.ToDateTime(new TimeOnly(22, 15));
            image.CameraGain = 200;
        });

        var detail = Detail(library, target.Id);

        Assert.Equal(100, detail.Gain);
        Assert.Equal(Day.ToDateTime(new TimeOnly(21, 0)), detail.FirstFrameTime);
        Assert.Equal(Day.ToDateTime(new TimeOnly(23, 30)), detail.LastFrameTime);
    }

    [Fact]
    public void Get_GainAndFrameTimes_NullCaptureDate_LeavesTheFirstTimeNull()
    {
        // Rule 5: a frame with no capture_date sorts first under SQLite's null ordering, and an
        // unknown start time stays unknown rather than borrowing the second frame's.
        using var library = Library.Empty();
        var target = library.AddTarget("Undated");
        library.AddFrame(target.Id, Day, image =>
        {
            Rig(image);
            image.CaptureDate = null;
            image.CameraGain = 50;
        });
        library.AddFrame(target.Id, Day, image =>
        {
            Rig(image);
            image.CaptureDate = Day.ToDateTime(new TimeOnly(22, 0));
            image.CameraGain = 100;
        });

        var detail = Detail(library, target.Id);

        Assert.Null(detail.FirstFrameTime);
        Assert.Equal(50, detail.Gain);
        Assert.Equal(Day.ToDateTime(new TimeOnly(22, 0)), detail.LastFrameTime);
    }

    // ---- per-filter blocks -------------------------------------------------------------

    [Fact]
    public void Get_FilterMedians_AreOnePerCanonicalFilter()
    {
        using var library = Library.Empty();
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new() { Color = "#3ba7ff", Aliases = ["O3"] },
        });
        var target = library.AddGroup(
            "Filters",
            Filtered("o3", 2d),
            Filtered("OIII", 4d),
            Filtered("Ha", 6d),
            Filtered(null, 100d),
            Filtered("", 100d));

        var detail = Detail(library, target);

        // Two raw spellings fold onto one canonical name, which is what makes the fold
        // load-bearing rather than decorative; a frame with no FILTER card gets no row.
        Assert.Equal(["Ha", "OIII"], detail.FilterMedians.Select(entry => entry.FilterName));
        var oiii = detail.FilterMedians.Single(entry => entry.FilterName == "OIII");
        Assert.Equal(3d, oiii.MedianHfr);

        static Action<Image> Filtered(string? filter, double hfr)
            => image =>
            {
                image.FilterUsed = filter;
                image.MedianHfr = hfr;
            };
    }

    [Fact]
    public void Get_FilterDetails_SplitByFilterAndExposure()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Split",
            Shot("L", 60d, 2d),
            Shot("L", 60d, 4d),
            Shot("L", 300d, 8d));

        var detail = Detail(library, target);

        Assert.Equal(2, detail.FilterDetails.Count);
        var shortRow = detail.FilterDetails[0];
        Assert.Equal("L", shortRow.FilterName);
        Assert.Equal(60d, shortRow.ExposureTime);
        Assert.Equal(2, shortRow.FrameCount);
        Assert.Equal(120d, shortRow.IntegrationSeconds);
        Assert.Equal(3d, shortRow.MedianHfr);

        var longRow = detail.FilterDetails[1];
        Assert.Equal(300d, longRow.ExposureTime);
        Assert.Equal(1, longRow.FrameCount);
        Assert.Equal(300d, longRow.IntegrationSeconds);
        Assert.Equal(8d, longRow.MedianHfr);

        // The per-filter medians block does not split by exposure: it is one row per filter.
        Assert.Single(detail.FilterMedians);
    }

    [Fact]
    public void Get_FilterDetails_AreOrderedByFilterThenExposure()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Ordering",
            Shot("L", 300d, 2d),
            Shot("Ha", 300d, 2d),
            Shot("L", 60d, 2d),
            Shot("Ha", null, 2d));

        var detail = Detail(library, target);

        Assert.Equal(
            [("Ha", (double?)null), ("Ha", 300d), ("L", 60d), ("L", 300d)],
            detail.FilterDetails.Select(row => (row.FilterName, row.ExposureTime)));
    }

    private static Action<Image> Shot(string filter, double? exposure, double hfr)
        => image =>
        {
            image.FilterUsed = filter;
            image.ExposureTime = exposure;
            image.MedianHfr = hfr;
        };

    // ---- weather ----------------------------------------------------------------------

    [Fact]
    public void Get_MedianAirmassAmbientTempAndHumidity_AreReported()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Weather",
            Weather(1.1d, 5d, 50d),
            Weather(1.2d, 6d, 60d),
            Weather(1.9d, 10d, 90d));

        var detail = Detail(library, target);

        Assert.Equal(1.2d, detail.MedianAirmass);
        Assert.Equal(6d, detail.MedianAmbientTemp);
        Assert.Equal(60d, detail.MedianHumidity);

        static Action<Image> Weather(double airmass, double ambient, double humidity)
            => image =>
            {
                image.Airmass = airmass;
                image.AmbientTemp = ambient;
                image.Humidity = humidity;
            };
    }

    // ---- frame rows --------------------------------------------------------------------

    [Fact]
    public void Get_FrameRows_CarryEveryFrameTableColumn()
    {
        // One frame with every column of spec 12.4's eight groups set to a distinct value, then
        // asserted field by field. This is what catches an ordinal slip in a 41 column reader,
        // which is the most likely defect in this task: two adjacent doubles swapped compile,
        // run, and report each other's numbers forever.
        using var library = Library.Empty();
        var target = library.AddTarget("Columns");
        var captured = Day.ToDateTime(new TimeOnly(22, 34, 56));
        var image = library.AddFrame(target.Id, Day, frame =>
        {
            frame.FilePath = @"C:\Frames\ngc7000-0001.fits";
            frame.FileName = "ngc7000-0001.fits";
            frame.CaptureDate = captured;
            frame.FilterUsed = "Ha";
            frame.ExposureTime = 301d;
            frame.MedianHfr = 2.34d;
            frame.Eccentricity = 0.41d;
            frame.EccentricitySource = "header";
            frame.Fwhm = 3.21d;
            frame.DetectedStars = 1234;
            frame.GuidingRmsArcsec = 0.51d;
            frame.GuidingRmsRaArcsec = 0.32d;
            frame.GuidingRmsDecArcsec = 0.43d;
            frame.GuidingRmsSource = "csv";
            frame.AduMean = 1001.5d;
            frame.AduMedian = 1002.5d;
            frame.AduStdev = 12.75d;
            frame.AduMin = 900;
            frame.AduMax = 4095;
            frame.FocuserPosition = 21456;
            frame.FocuserTemp = 4.5d;
            frame.AmbientTemp = 6.5d;
            frame.DewPoint = 1.5d;
            frame.Humidity = 62.5d;
            frame.Pressure = 1013.25d;
            frame.WindSpeed = 3.5d;
            frame.WindDirection = 271.5d;
            frame.WindGust = 7.5d;
            frame.CloudCover = 12.5d;
            frame.SkyQuality = 21.35d;
            frame.Airmass = 1.15d;
            frame.PierSide = "EAST";
            frame.RotatorPosition = 89.5d;
            frame.SensorTemp = -10.5d;
            frame.CameraGain = 121;
            frame.Telescope = "RC8";
            frame.Camera = "ASI2600MM";
            frame.RaDeg = 314.75d;
            frame.DecDeg = 44.5d;
        });

        var row = Assert.Single(Detail(library, target.Id).Frames);

        Assert.Equal(image.Id, row.ImageId);
        Assert.Equal(@"C:\Frames\ngc7000-0001.fits", row.FilePath);
        Assert.Equal("ngc7000-0001.fits", row.FileName);
        Assert.Equal(captured, row.CaptureDate);
        Assert.Equal("Ha", row.FilterUsed);
        Assert.Equal(301d, row.ExposureTime);
        Assert.Equal(2.34d, row.MedianHfr);
        Assert.Equal(0.41d, row.Eccentricity);
        Assert.Equal(3.21d, row.Fwhm);
        Assert.Equal(1234, row.DetectedStars);
        Assert.Equal(0.51d, row.GuidingRmsArcsec);
        Assert.Equal(0.32d, row.GuidingRmsRaArcsec);
        Assert.Equal(0.43d, row.GuidingRmsDecArcsec);
        Assert.Equal("csv", row.GuidingRmsSource);
        Assert.Equal(1001.5d, row.AduMean);
        Assert.Equal(1002.5d, row.AduMedian);
        Assert.Equal(12.75d, row.AduStdev);
        Assert.Equal(900, row.AduMin);
        Assert.Equal(4095, row.AduMax);
        Assert.Equal(21456, row.FocuserPosition);
        Assert.Equal(4.5d, row.FocuserTemp);
        Assert.Equal(6.5d, row.AmbientTemp);
        Assert.Equal(1.5d, row.DewPoint);
        Assert.Equal(62.5d, row.Humidity);
        Assert.Equal(1013.25d, row.Pressure);
        Assert.Equal(3.5d, row.WindSpeed);
        Assert.Equal(271.5d, row.WindDirection);
        Assert.Equal(7.5d, row.WindGust);
        Assert.Equal(12.5d, row.CloudCover);
        Assert.Equal(21.35d, row.SkyQuality);
        Assert.Equal(1.15d, row.Airmass);
        Assert.Equal("EAST", row.PierSide);
        Assert.Equal(89.5d, row.RotatorPosition);
        Assert.Equal(-10.5d, row.SensorTemp);
        Assert.Equal(121, row.CameraGain);
        Assert.Equal("RC8 / ASI2600MM", row.Rig);
        Assert.Equal(314.75d, row.RaDeg);
        Assert.Equal(44.5d, row.DecDeg);
    }

    [Fact]
    public void Get_FrameRows_CarryEccentricitySourceAndArcsecPerPixel()
    {
        // P25 R3: the two inputs the merge pools on ride on the row, read from the same two
        // columns Row already carried; neither is a frame table column.
        using var library = Library.Empty();
        var target = library.AddTarget("Merge inputs");
        library.AddFrame(target.Id, Day, frame =>
        {
            Rig(frame);
            frame.MedianHfr = 2.0d;
            frame.Eccentricity = 0.5d;
            frame.EccentricitySource = "sep";
            frame.ArcsecPerPixel = 1.25d;
        });

        var row = Assert.Single(Detail(library, target.Id).Frames);

        Assert.Equal("sep", row.EccentricitySource);
        Assert.Equal(1.25d, row.ArcsecPerPixel);
    }

    [Fact]
    public void Get_FrameRows_AreInCaptureOrder()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("CaptureOrder");
        foreach (var minute in new[] { 40, 10, 30, 20 })
        {
            library.AddFrame(target.Id, Day, image =>
            {
                Rig(image);
                image.FileName = $"frame-{minute:D2}.fits";
                image.CaptureDate = Day.ToDateTime(new TimeOnly(21, minute));
            });
        }

        var detail = Detail(library, target.Id);

        Assert.Equal(
            ["frame-10.fits", "frame-20.fits", "frame-30.fits", "frame-40.fits"],
            detail.Frames.Select(frame => frame.FileName));
    }

    [Fact]
    public void Get_FrameRows_GuidingRmsSource_IsSurfaced()
    {
        // Spec 12.4: the cell shows a source glyph when the column is set, so a CSV figure is
        // never silently compared with another source. Null must stay null, not become "".
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Guiding",
            image =>
            {
                image.CaptureDate = Day.ToDateTime(new TimeOnly(21, 0));
                image.GuidingRmsArcsec = 0.5d;
                image.GuidingRmsSource = "csv";
            },
            image =>
            {
                image.CaptureDate = Day.ToDateTime(new TimeOnly(22, 0));
                image.GuidingRmsArcsec = 0.6d;
                image.GuidingRmsSource = null;
            });

        var detail = Detail(library, target);

        Assert.Equal("csv", detail.Frames[0].GuidingRmsSource);
        Assert.Null(detail.Frames[1].GuidingRmsSource);
    }

    [Fact]
    public void Get_FrameRows_Rig_UsesCanonicalNamesAndUnknownForAMissingHalf()
    {
        using var library = Library.Empty();
        library.Settings.SaveEquipment(new EquipmentSettings
        {
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new() { Aliases = ["GSO RC8"] },
            },
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new() { Aliases = ["ZWO ASI2600MM Pro"] },
            },
        });
        var target = library.AddTarget("Rigs");
        library.AddFrame(target.Id, Day, image =>
        {
            image.CaptureDate = Day.ToDateTime(new TimeOnly(21, 0));
            image.Telescope = "GSO RC8";
            image.Camera = "ZWO ASI2600MM Pro";
        });
        library.AddFrame(target.Id, Day, image =>
        {
            image.CaptureDate = Day.ToDateTime(new TimeOnly(22, 0));
            image.Telescope = null;
            image.Camera = "ZWO ASI2600MM Pro";
        });
        library.AddFrame(target.Id, Day, image =>
        {
            image.CaptureDate = Day.ToDateTime(new TimeOnly(23, 0));
            image.Telescope = null;
            image.Camera = null;
        });

        var detail = Detail(library, target.Id);

        Assert.Equal(
            ["RC8 / ASI2600MM", "Unknown / ASI2600MM", "Unknown / Unknown"],
            detail.Frames.Select(frame => frame.Rig));
    }

    // ---- notes ------------------------------------------------------------------------

    [Fact]
    public void Get_Notes_ReturnsTheSessionNoteText()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Noted", Hfr(2d));
        library.AddNote(target, Day, "clouds after 02:00");
        // A note on the neighbouring night must not leak into this card.
        library.AddNote(target, Day.AddDays(1), "clear all night");

        Assert.Equal("clouds after 02:00", Detail(library, target).Notes);
    }

    [Fact]
    public void Get_Notes_UnresolvedGroup_IsNull()
    {
        // An obj: group has no target id, so there is no session_notes row to key on and the
        // statement is not even emitted.
        using var library = Library.Empty();
        library.AddFrame(null, Day, image =>
        {
            Rig(image);
            image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155");
        });

        var detail = library.Query.Get("obj:Sh2-155", Day);

        Assert.NotNull(detail);
        Assert.Null(detail.Notes);
        Assert.Equal(1, detail.FrameCount);
    }

    // ---- eccentricity pooling (spec 7.2) -----------------------------------------------

    [Fact]
    public void Get_Eccentricity_PoolsOnlyTheModalSourceAndDisclosesIt()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Sources",
            Ecc(0.30d),
            Ecc(0.40d),
            Ecc(0.50d),
            Ecc(0.99d, "ellipticity"),
            Ecc(0.01d, "ellipticity"));

        var detail = Detail(library, target);

        // The two ellipticity frames are the widest and the narrowest in the session, so a pooled
        // range would report 0.01 to 0.99 rather than the header source's own 0.30 to 0.50.
        AssertRange(detail.Eccentricity, 0.30d, 0.50d, 0.40d);
        Assert.Equal("header", detail.EccentricitySource);
        Assert.Equal(2, detail.EccentricityExcludedCount);
    }

    [Fact]
    public void Get_Eccentricity_PerFilterMediansAndDetailsPoolTheSameSource()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "SourcesPerFilter",
            Both("L", 0.30d, "header"),
            Both("L", 0.40d, "header"),
            Both("L", 0.99d, "ellipticity"));

        var detail = Detail(library, target);

        Assert.Equal(0.35d, Assert.Single(detail.FilterMedians).MedianEccentricity);
        Assert.Equal(0.35d, Assert.Single(detail.FilterDetails).MedianEccentricity);

        static Action<Image> Both(string filter, double eccentricity, string source)
            => image =>
            {
                image.FilterUsed = filter;
                image.Eccentricity = eccentricity;
                image.EccentricitySource = source;
            };
    }

    // ---- insights ---------------------------------------------------------------------

    [Fact]
    public void Get_Insights_HfrOutliers_FireAboveOnePointFiveTimesTheMedian()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Outliers", Hfr(2d), Hfr(2d), Hfr(2d), Hfr(4d));

        var detail = Detail(library, target);

        // Median 2.0, threshold 3.0, one frame strictly above it.
        Assert.Equal("1 frame with HFR outlier (> 3.0)", Message(detail, "hfr_outliers"));
        Assert.Equal(InsightLevel.Warning, Assert.Single(detail.Insights).Level);
    }

    [Fact]
    public void Get_Insights_HfrOutliers_SilentBelowThreeMeasuredFrames()
    {
        using var library = Library.Empty();
        // Two frames carry an HFR and a third carries none: the rule counts measurements, not
        // frames, so this stays silent.
        var target = library.AddGroup("TooFew", Hfr(2d), Hfr(40d), image => image.MedianHfr = null);

        Assert.Empty(Detail(library, target).Insights);
    }

    [Fact]
    public void Get_Insights_HfrOutliers_SilentWhenNoFrameExceedsTheThreshold()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Tidy", Hfr(2d), Hfr(2d), Hfr(2.5d));

        Assert.Empty(Detail(library, target).Insights);
    }

    [Fact]
    public void Get_Insights_HfrOutliers_MessageMatchesTheSpecifiedText()
    {
        using var singular = Library.Empty();
        var one = singular.AddGroup("One", Hfr(2d), Hfr(2d), Hfr(2d), Hfr(4d));
        Assert.Equal("1 frame with HFR outlier (> 3.0)", Message(Detail(singular, one), "hfr_outliers"));

        using var plural = Library.Empty();
        var many = plural.AddGroup("Many", Hfr(2d), Hfr(2d), Hfr(2d), Hfr(4d), Hfr(5d));
        // The plural s appears on both "frames" and "outliers".
        Assert.Equal("2 frames with HFR outliers (> 3.0)", Message(Detail(plural, many), "hfr_outliers"));
    }

    [Fact]
    public void Get_Insights_EccentricityOutliers_UseTheSessionBaseline()
    {
        using var library = Library.Empty();
        // Nine frames in one group: median 20, MAD 10, N 9. The 200 frame is 18 MAD out.
        var target = library.AddGroup("Elongated", [.. EccentricitySpread(200d)]);

        var detail = Detail(library, target);

        Assert.Equal(
            "1 frame with eccentricity outlier (>= 3 MAD above the group median)",
            Message(detail, "eccentricity_outliers"));
    }

    [Fact]
    public void Get_Insights_EccentricityOutliers_SilentWhenTheGroupIsTooSparseToGrade()
    {
        using var library = Library.Empty();
        // Seven measurements is below FrameQuality.MinGroup, so nothing can be judged and the
        // query says nothing rather than announcing a clean night it could not verify.
        var target = library.AddGroup(
            "Sparse",
            Ecc(10d), Ecc(10d), Ecc(10d),
            Ecc(20d), Ecc(20d), Ecc(20d),
            Ecc(200d));

        Assert.DoesNotContain(
            Detail(library, target).Insights,
            insight => insight.Kind == "eccentricity_outliers");
    }

    /// <summary>Eight frames at 10 and 20 plus one at <paramref name="outlier"/>: median 20,
    /// MAD 10, N 9, all values exact in binary so the z-score is not a rounding artefact.</summary>
    private static IEnumerable<Action<Image>> EccentricitySpread(double outlier)
        => [.. Enumerable.Range(0, 8).Select(i => Ecc(i < 4 ? 10d : 20d)), Ecc(outlier)];

    [Fact]
    public void Get_Insights_EccentricityVsRig_FiresWhenTheWholeNightIsElongated()
    {
        using var library = Library.Empty();
        // Library history for this rig and filter: ten frames at 10 and 20.
        library.AddHistory("History", [.. Enumerable.Range(0, 10).Select(i => Ecc(i < 5 ? 10d : 20d))]);
        // Tonight: three frames, every one of them elongated, which is exactly the case the
        // per-frame z-score is blind to because the session median moves with the bad frames.
        var target = library.AddGroup("WholeNight", Ecc(90d), Ecc(90d), Ecc(90d));

        var detail = Detail(library, target);

        // Rig baseline over the 13 library frames: median 20, MAD 10, N 13. (90 - 20) / 10 = 7.
        Assert.Equal(
            "[Tel / Cam / L] Whole session is elongated: median eccentricity 90.00 vs 20.00 "
                + "typical for this rig (7.0 MAD above normal)",
            Message(detail, "eccentricity_vs_rig"));
    }

    [Fact]
    public void Get_Insights_EccentricityVsRig_SilentBelowThreeFramesInTheGroup()
    {
        using var library = Library.Empty();
        library.AddHistory("History", [.. Enumerable.Range(0, 10).Select(i => Ecc(i < 5 ? 10d : 20d))]);
        // Two frames is below FrameQuality.MinSessionMedianFrames: a whole-night claim needs a
        // median resting on more than one or two frames.
        var target = library.AddGroup("Thin", Ecc(90d), Ecc(90d));

        Assert.DoesNotContain(
            Detail(library, target).Insights,
            insight => insight.Kind == "eccentricity_vs_rig");
    }

    [Fact]
    public void Get_Insights_EccentricityVsRig_SilentWhenTheLibraryPooledADifferentSource()
    {
        using var library = Library.Empty();
        // The library's modal source is "ellipticity" (ten frames against three), so the rig
        // baseline describes that scale. Comparing tonight's "header" median against it would be
        // a spec 7.2 violation with a very loud number attached: z would be 87.
        library.AddHistory(
            "History",
            [.. Enumerable.Range(0, 10).Select(i => Ecc(i < 5 ? 2d : 4d, "ellipticity"))]);
        var target = library.AddGroup("Mismatched", Ecc(90d), Ecc(90d), Ecc(90d));

        var detail = Detail(library, target);

        Assert.Equal("header", detail.EccentricitySource);
        Assert.DoesNotContain(detail.Insights, insight => insight.Kind == "eccentricity_vs_rig");
    }

    // FIXER LIST F1. RigBaselinesCache.Load applies spec 7.2 on the library side too: a frame
    // outside the library's modal eccentricity source contributes its other metrics and a null
    // eccentricity. Nothing pinned that filter, and dropping it does not silence the insight, it
    // silently reports a different verdict.
    [Fact]
    public void Get_Insights_EccentricityVsRig_RigBaselinePoolsOnlyTheModalSource()
    {
        using var library = Library.Empty();

        // One rig group, two sources in it. "header" is the modal one, 13 frames against 12, so
        // the session under test is comparable against the baseline and the source-mismatch guard
        // stays out of the way; this is a test of the Load-side filter, not of that guard.
        library.AddHistory("Header", [.. Enumerable.Range(0, 10).Select(i => Ecc(i < 5 ? 10d : 20d))]);

        // A minority source on a wildly different scale, which is the whole reason spec 7.2 pools:
        // in the baseline these twelve values drag the median to 90 and the MAD to 80, and a
        // z-score of 0 replaces the real verdict with silence.
        library.AddHistory(
            "Ellipticity",
            [.. Enumerable.Range(0, 12).Select(_ => Ecc(1000d, "ellipticity"))]);

        var target = library.AddGroup("WholeNight", Ecc(90d), Ecc(90d), Ecc(90d));

        var detail = Detail(library, target);

        // Pooled on "header" alone: 13 values, median 20, MAD 10, (90 - 20) / 10 = 7.
        Assert.Equal("header", detail.EccentricitySource);
        Assert.Equal(
            "[Tel / Cam / L] Whole session is elongated: median eccentricity 90.00 vs 20.00 "
                + "typical for this rig (7.0 MAD above normal)",
            Message(detail, "eccentricity_vs_rig"));
    }

    [Fact]
    public void Get_Insights_MultiRigSession_EmitsPerRigInsightsWithTheRigPrefix()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("TwoRigs");
        foreach (var hfr in new[] { 2d, 2d, 2d, 9d })
        {
            AddRigFrame(library, target.Id, "ATel", "ACam", hfr);
        }

        foreach (var hfr in new[] { 4d, 4d, 4d, 20d })
        {
            AddRigFrame(library, target.Id, "BTel", "BCam", hfr);
        }

        var detail = Detail(library, target.Id);

        // Session-wide median is 4.0 across both rigs, so its threshold is 6.0 and it counts two
        // frames; each rig graded against its own median counts one. That divergence is the whole
        // reason the per-rig lines exist.
        Assert.Equal(
            [
                "2 frames with HFR outliers (> 6.0)",
                "[ATel / ACam] 1 frame with HFR outlier (> 3.0)",
                "[BTel / BCam] 1 frame with HFR outlier (> 6.0)",
            ],
            detail.Insights.Select(insight => insight.Message));
    }

    [Fact]
    public void Get_Insights_SingleRigSession_EmitsNoPrefixedInsight()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("OneRig", Hfr(2d), Hfr(2d), Hfr(2d), Hfr(4d));

        Assert.DoesNotContain(
            Detail(library, target).Insights,
            insight => insight.Message.StartsWith('['));
    }

    [Fact]
    public void Get_Insights_AreInADeterministicOrder()
    {
        using var library = Library.Empty();
        // History widens rig A's library baseline (median 4, MAD 2, N 19) so the whole-night
        // check has something to fire against. The history has to carry rig A's own equipment,
        // because the baselines are keyed per (telescope, camera, filter) and history on another
        // rig widens another group.
        //
        // FIXER LIST F4: rig B is graded too, not ungraded. Its nine frames of tonight are its
        // whole library group (median 20, MAD 10, N 9), so its session median of 20 scores z 0 and
        // the whole-night line stays silent on the number, not for want of a baseline.
        library.AddHistory(
            "History",
            [.. Enumerable.Range(0, 10).Select(i => (Action<Image>)(image =>
            {
                image.Telescope = "ATel";
                image.Camera = "ACam";
                image.FilterUsed = "L";
                image.Eccentricity = i < 5 ? 2d : 4d;
                image.EccentricitySource = "header";
            }))]);
        var target = library.AddTarget("Everything");
        foreach (var (telescope, camera) in new[] { ("ATel", "ACam"), ("BTel", "BCam") })
        {
            for (var i = 0; i < 9; i++)
            {
                var eccentricity = i < 4 ? 10d : i < 8 ? 20d : 200d;
                var hfr = i < 8 ? 2d : 9d;
                AddRigFrame(library, target.Id, telescope, camera, hfr, eccentricity);
            }
        }

        var detail = Detail(library, target.Id);

        Assert.Equal(
            [
                "hfr_outliers",
                "eccentricity_outliers",
                "eccentricity_vs_rig",
                "hfr_outliers",
                "eccentricity_outliers",
                "hfr_outliers",
                "eccentricity_outliers",
            ],
            detail.Insights.Select(insight => insight.Kind));

        // The per-rig block is grouped by rig label ascending, session-wide lines first.
        Assert.Equal(
            ["", "", "[ATel / ACam / L] ", "[ATel / ACam] ", "[ATel / ACam] ", "[BTel / BCam] ", "[BTel / BCam] "],
            detail.Insights.Select(insight =>
                insight.Message.StartsWith('[')
                    ? insight.Message[..(insight.Message.IndexOf("] ", StringComparison.Ordinal) + 2)]
                    : ""));
    }

    [Fact]
    public void Get_Insights_MessagesUseInvariantCulture()
    {
        // On a machine set to de-DE an ambient-culture format puts a comma where every assertion
        // above expects a decimal point, and the message would read "(> 3,0)".
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            using var library = Library.Empty();
            library.AddHistory("History", [.. Enumerable.Range(0, 10).Select(i => Ecc(i < 5 ? 10d : 20d))]);
            var target = library.AddGroup(
                "Culture",
                Both(2d, 90d),
                Both(2d, 90d),
                Both(2d, 90d),
                Both(4d, 90d));

            var detail = Detail(library, target);

            Assert.Equal("1 frame with HFR outlier (> 3.0)", Message(detail, "hfr_outliers"));
            Assert.Contains("90.00 vs 20.00", Message(detail, "eccentricity_vs_rig"));
            Assert.Contains("(7.0 MAD above normal)", Message(detail, "eccentricity_vs_rig"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        static Action<Image> Both(double hfr, double eccentricity)
            => image =>
            {
                image.MedianHfr = hfr;
                image.Eccentricity = eccentricity;
                image.EccentricitySource = "header";
            };
    }

    private static void AddRigFrame(
        Library library,
        Guid targetId,
        string telescope,
        string camera,
        double hfr,
        double? eccentricity = null)
        => library.AddFrame(targetId, Day, image =>
        {
            image.Telescope = telescope;
            image.Camera = camera;
            image.FilterUsed = "L";
            image.MedianHfr = hfr;
            image.Eccentricity = eccentricity;
            image.EccentricitySource = eccentricity is null ? null : "header";
        });

    // ---- per-frame outlier flags (P12 R3) ----------------------------------------------
    //
    // R3's whole claim is that the flagged rows and the insight counts cannot disagree, so every
    // case below recomputes the expected number from something other than the flags: the HFR side
    // from a median worked out in the test, the eccentricity side from FrameQuality's own
    // CountOutliers over frames rebuilt from the query's output.

    /// <summary>A median written out in the test rather than taken from
    /// <c>Statistics.Median</c>: the assertion is only worth making if the expected count comes
    /// from somewhere other than the code under test.</summary>
    private static double? PlainMedian(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static void AssertHfrFlagsAgreeWithTheInsight(SessionDetail detail)
    {
        var values = detail.Frames.Select(frame => frame.MedianHfr).OfType<double>().ToList();
        var expected = 0;
        if (values.Count > 2 && PlainMedian(values) is { } median)
        {
            var threshold = median * 1.5d;
            expected = values.Count(value => value > threshold);
        }

        Assert.Equal(expected, detail.Frames.Count(frame => frame.IsHfrOutlier));
        Assert.Equal(expected > 0, detail.Insights.Any(insight => insight.Kind == "hfr_outliers"));
    }

    /// <summary>Rebuilds the graded frames from the returned rows and asks the Core rule for the
    /// count, so the expected number comes from <c>FrameQuality</c> and the actual number from the
    /// query's flags. Valid only on a fixture where every eccentricity shares one source, because
    /// <see cref="FrameRow"/> carries no source column and the pooled value would otherwise differ
    /// from the raw one.</summary>
    private static void AssertEccentricityFlagsAgreeWithTheInsight(SessionDetail detail)
    {
        var graded = detail.Frames.Select(frame =>
        {
            var rig = frame.Rig.Split(" / ");
            return new GradedFrame(
                rig[0],
                rig[1],
                frame.FilterUsed,
                frame.MedianHfr,
                frame.Fwhm,
                frame.Eccentricity,
                frame.DetectedStars,
                frame.AduMedian,
                frame.GuidingRmsArcsec);
        }).ToList();
        var (expected, _) = FrameQuality.CountOutliers(
            graded,
            FrameQuality.GroupBaselines(graded),
            FrameQuality.EccentricityMetric);

        Assert.Equal(expected, detail.Frames.Count(frame => frame.IsEccentricityOutlier));
        Assert.Equal(expected > 0, detail.Insights.Any(insight => insight.Kind == "eccentricity_outliers"));
    }

    [Fact]
    public void Get_FlaggedHfrRows_MatchTheHfrOutlierInsightCount()
    {
        using var library = Library.Empty();
        var target = library.AddGroup("Flagged", Hfr(2d), Hfr(2d), Hfr(2d), Hfr(4d), Hfr(5d));

        var detail = Detail(library, target);

        // Median 2.0, threshold 3.0, two frames above it, and those two are the flagged rows.
        Assert.Equal(2, detail.Frames.Count(frame => frame.IsHfrOutlier));
        Assert.Equal("2 frames with HFR outliers (> 3.0)", Message(detail, "hfr_outliers"));
        Assert.All(
            detail.Frames.Where(frame => frame.IsHfrOutlier),
            frame => Assert.True(frame.MedianHfr > 3d));
        AssertHfrFlagsAgreeWithTheInsight(detail);
    }

    [Fact]
    public void Get_OnTheSeededLibrary_FlaggedRowsMatchBothInsightCounts()
    {
        // The populated fixture session the roadmap's Verify clause names. Session index 0 of
        // Targets[0] is a single-rig night, so the session-wide insight is the only one of each
        // kind and the flags have nothing to disagree with.
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[0];

        var detail = Detail(library, seeded.Id, seeded.FirstSession);

        AssertHfrFlagsAgreeWithTheInsight(detail);
        AssertEccentricityFlagsAgreeWithTheInsight(detail);
    }

    [Fact]
    public void Get_FlaggedEccentricityRows_MatchTheEccentricityOutlierInsightCount()
    {
        using var library = Library.Empty();
        // Nine frames in one group: median 20, MAD 10, N 9. The 200 frame is 18 MAD out.
        var target = library.AddGroup("FlaggedEccentricity", [.. EccentricitySpread(200d)]);

        var detail = Detail(library, target);

        Assert.Equal(1, detail.Frames.Count(frame => frame.IsEccentricityOutlier));
        Assert.Equal(200d, Assert.Single(detail.Frames, frame => frame.IsEccentricityOutlier).Eccentricity);
        AssertEccentricityFlagsAgreeWithTheInsight(detail);
    }

    [Fact]
    public void Get_ASessionWithNoOutliers_FlagsNoRow()
    {
        using var library = Library.Empty();
        // Nine identical frames: the HFR threshold is never exceeded, and a uniform group has a
        // MAD of 0, so MadZ returns null and nothing can be an eccentricity outlier either.
        var uniform = new Action<Image>[9];
        Array.Fill(uniform, image =>
        {
            image.MedianHfr = 2d;
            Ecc(0.4d)(image);
        });
        var target = library.AddGroup("Uniform", uniform);

        var detail = Detail(library, target);

        Assert.Equal(9, detail.Frames.Count);
        Assert.All(detail.Frames, frame => Assert.False(frame.IsHfrOutlier));
        Assert.All(detail.Frames, frame => Assert.False(frame.IsEccentricityOutlier));
        Assert.DoesNotContain(detail.Insights, insight => insight.Kind == "hfr_outliers");
        Assert.DoesNotContain(detail.Insights, insight => insight.Kind == "eccentricity_outliers");
    }

    [Fact]
    public void Get_ASessionWithTwoFrames_FlagsNoHfrRow()
    {
        using var library = Library.Empty();
        // The insight's own guard: two measurements is below three, so it stays silent and no row
        // may be flagged however wild the second frame is.
        var target = library.AddGroup("TwoFrames", Hfr(2d), Hfr(40d));

        var detail = Detail(library, target);

        Assert.DoesNotContain(detail.Insights, insight => insight.Kind == "hfr_outliers");
        Assert.All(detail.Frames, frame => Assert.False(frame.IsHfrOutlier));
        AssertHfrFlagsAgreeWithTheInsight(detail);
    }

    [Fact]
    public void Get_AFrameFromANonModalEccentricitySource_IsNeverFlagged()
    {
        using var library = Library.Empty();
        // Eight frames on the modal source (median 15, MAD 5, N 8) plus one wild frame from
        // another source. Spec 7.2 leaves that frame's pooled eccentricity null, so it is not
        // graded and cannot be an outlier whatever its raw value.
        var target = library.AddGroup(
            "NonModal",
            [
                .. Enumerable.Range(0, 8).Select(i => Ecc(i < 4 ? 10d : 20d)),
                Ecc(9999d, "sextractor"),
            ]);

        var detail = Detail(library, target);

        var stranger = Assert.Single(detail.Frames, frame => frame.Eccentricity == 9999d);
        Assert.False(stranger.IsEccentricityOutlier);
        Assert.All(detail.Frames, frame => Assert.False(frame.IsEccentricityOutlier));
        Assert.DoesNotContain(detail.Insights, insight => insight.Kind == "eccentricity_outliers");
    }

    // ---- the P24 R22 flags: the eccentricity rule over the other graded metrics -----------

    [Fact]
    public void Get_FlaggedFwhmRows_AreTheFramesAtOrBeyondThreeMadAboveTheGroupMedian()
    {
        using var library = Library.Empty();
        // Nine frames: median 3, MAD 1, N 9. The 30 frame is 27 MAD out; a 2 frame is 1 MAD under.
        var target = library.AddGroup("FlaggedFwhm", [.. Enumerable.Range(0, 8).Select(i => Fwhm(i < 4 ? 2d : 3d)), Fwhm(30d)]);

        var detail = Detail(library, target);

        // A failure looks like no row flagged, or the 2 frames flagged beside the 30.
        Assert.Equal(30d, Assert.Single(detail.Frames, frame => frame.IsFwhmOutlier).Fwhm);
        Assert.DoesNotContain(detail.Insights, insight => insight.Kind.Contains("fwhm", StringComparison.Ordinal));
    }

    [Fact]
    public void Get_FlaggedStarsRows_AreBelowTheGroupMedian_NeverAbove()
    {
        using var library = Library.Empty();
        // Ten frames: median 1100, MAD 100. The 100 frame is 10 MAD under, the 5000 frame 39 MAD
        // over, and only few stars is the bad side.
        var target = library.AddGroup(
            "FlaggedStars",
            [.. Enumerable.Range(0, 8).Select(i => Stars(i < 4 ? 1000 : 1200)), Stars(100), Stars(5000)]);

        var detail = Detail(library, target);

        // A failure looks like the 5000 frame flagged, or the 100 frame not flagged.
        Assert.Equal(100, Assert.Single(detail.Frames, frame => frame.IsStarsOutlier).DetectedStars);
    }

    [Fact]
    public void Get_FlaggedGuidingRmsRows_AreTheFramesAtOrBeyondThreeMadAboveTheGroupMedian()
    {
        using var library = Library.Empty();
        // Nine frames: median 0.7, MAD 0.2, N 9. The 5.0 frame is 21.5 MAD out.
        var target = library.AddGroup("FlaggedRms", [.. Enumerable.Range(0, 8).Select(i => Rms(i < 4 ? 0.5d : 0.7d)), Rms(5d)]);

        var detail = Detail(library, target);

        // A failure looks like no row flagged, or a 0.5 frame flagged beside the 5.0.
        Assert.Equal(5d, Assert.Single(detail.Frames, frame => frame.IsGuidingRmsOutlier).GuidingRmsArcsec);
    }

    [Fact]
    public void Get_ASparseOrAbsentMetric_FlagsNoRow()
    {
        using var library = Library.Empty();
        // Seven frames carry a FWHM, under MinGroup, and none carries stars or a guiding RMS.
        var target = library.AddGroup("SparseFwhm", [.. Enumerable.Range(0, 6).Select(i => Fwhm(2d + (i % 2))), Fwhm(90d)]);

        var detail = Detail(library, target);

        Assert.All(detail.Frames, frame => Assert.False(frame.IsFwhmOutlier || frame.IsStarsOutlier || frame.IsGuidingRmsOutlier));
    }

    // ---- RigBaselinesCache -------------------------------------------------------------

    [Fact]
    public void RigBaselinesCache_SecondReadInsideTheTtl_DoesNotRequery()
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var library = Library.Empty(() => now);
        var target = library.AddTarget("Cached");
        library.AddFrame(target.Id, Day, Rig);

        Assert.Equal(["Tel|Cam|L"], library.Baselines.Current.Groups.Keys);

        // A frame that appears between the two reads is invisible to the second one, which is the
        // observable proof the images table was not rescanned.
        library.AddFrame(target.Id, Day, image =>
        {
            image.Telescope = "Other";
            image.Camera = "Other";
            image.FilterUsed = "Ha";
        });
        Assert.Equal(["Tel|Cam|L"], library.Baselines.Current.Groups.Keys);

        // Past the TTL the backstop rebuilds even with no explicit invalidation.
        now = now.Add(RigBaselinesCache.Ttl);
        Assert.Equal(["Other|Other|Ha", "Tel|Cam|L"], library.Baselines.Current.Groups.Keys.Order());
    }

    [Fact]
    public void RigBaselinesCache_Invalidate_ForcesARebuild()
    {
        // A frozen clock, so only the explicit invalidation can be what rebuilt the value: the
        // TTL is the backstop, not the mechanism a finished scan relies on.
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var library = Library.Empty(() => now);
        var target = library.AddTarget("Invalidated");
        library.AddFrame(target.Id, Day, Rig);

        Assert.Single(library.Baselines.Current.Groups);

        library.AddFrame(target.Id, Day, image =>
        {
            image.Telescope = "Other";
            image.Camera = "Other";
            image.FilterUsed = "Ha";
        });
        library.Baselines.Invalidate();

        Assert.Equal(2, library.Baselines.Current.Groups.Count);
    }

    [Fact]
    public void RigBaselinesCache_FoldsEquipmentAndFiltersThroughTheAliasMap()
    {
        // The session baselines fold before keying, so the rig baseline has to fold too or the
        // two dictionaries never meet and every whole-night check silently misses.
        using var library = Library.Empty();
        library.Settings.SaveEquipment(new EquipmentSettings
        {
            Telescopes = new Dictionary<string, EquipmentItemSettings> { ["Tel"] = new() { Aliases = ["Telescope 1"] } },
            Cameras = new Dictionary<string, EquipmentItemSettings> { ["Cam"] = new() { Aliases = ["Camera 1"] } },
        });
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["L"] = new() { Color = "#cccccc", Aliases = ["Lum"] },
        });
        var target = library.AddTarget("Aliased");
        library.AddFrame(target.Id, Day, image =>
        {
            image.Telescope = "Telescope 1";
            image.Camera = "Camera 1";
            image.FilterUsed = "Lum";
        });
        library.AddFrame(target.Id, Day, Rig);

        Assert.Equal(["Tel|Cam|L"], library.Baselines.Current.Groups.Keys);
    }

    // ---- the seeded library ------------------------------------------------------------

    [Fact]
    public void Get_OnTheSeededLibrary_ReportsTheAggregatesTheSeederDeclares()
    {
        // Session index 0 of the fixture: 22 frames of Targets[0] on rig 0, all five filters, one
        // exposure, one gain, one plate scale. Every figure below is derived from the seeder's
        // published generation rules, so a forgotten CloneImage column fails here.
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[0];

        var detail = Detail(library, seeded.Id, seeded.FirstSession);

        Assert.Equal(22, detail.FrameCount);
        Assert.Equal(22 * LibrarySeeder.ExposureSeconds, detail.IntegrationSeconds);
        Assert.Equal([LibrarySeeder.ExposureSeconds], detail.ExposureTimes);
        Assert.Equal(LibrarySeeder.Gain, detail.Gain);
        Assert.Null(detail.Notes);

        // hfr = 2.0 + (j % 5) * 0.1 over j = 0..21.
        AssertRange(detail.Hfr, 2.0d, 2.4d, 2.2d);
        Assert.Equal(2.2d * LibrarySeeder.ArcsecPerPixelRig0, detail.MedianHfrArcsec!.Value, 10);
        Assert.Equal(0, detail.HfrArcsecExcludedCount);

        // eccentricity = 0.30 + (j % 3) * 0.05, every frame on the modal source.
        AssertRange(detail.Eccentricity, 0.30d, 0.40d, 0.35d);
        Assert.Equal(LibrarySeeder.EccentricitySourceHeader, detail.EccentricitySource);
        Assert.Equal(0, detail.EccentricityExcludedCount);

        AssertRange(detail.Fwhm, 3.0d, 3.75d, 3.25d);
        AssertRange(detail.GuidingRmsArcsec, 0.45d, 0.60d, 0.50d);
        AssertRange(detail.SensorTemp, -10.0d, -9.0d, -9.5d);

        // The three Task 2 additions to the fixture. Non-null is the CloneImage guard; the exact
        // medians are the generation rules.
        Assert.Equal(1.15d, detail.MedianAirmass);
        Assert.Equal(7.5d, detail.MedianAmbientTemp);
        Assert.Equal(57.5d, detail.MedianHumidity);

        // All five filters, ordered by name; one detail row each because there is one exposure.
        Assert.Equal(["B", "G", "Ha", "L", "R"], detail.FilterMedians.Select(entry => entry.FilterName));
        Assert.Equal(["B", "G", "Ha", "L", "R"], detail.FilterDetails.Select(row => row.FilterName));
        Assert.All(detail.FilterDetails, row => Assert.Equal(LibrarySeeder.ExposureSeconds, row.ExposureTime));
        Assert.Equal(22, detail.FilterDetails.Sum(row => row.FrameCount));

        Assert.Equal(22, detail.Frames.Count);
        Assert.All(detail.Frames, frame => Assert.Equal("RC8 / ASI2600MM", frame.Rig));
    }

    [Fact]
    public void Get_OnTheSeededLibrary_SessionWithoutAPlateScale_ExcludesEveryFrame()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[1];

        var detail = Detail(library, seeded.Id, LibrarySeeder.SessionWithoutPlateScale);

        Assert.Equal(LibrarySeeder.FramesWithoutPlateScale, detail.FrameCount);
        Assert.Null(detail.MedianHfrArcsec);
        Assert.Equal(LibrarySeeder.FramesWithoutPlateScale, detail.HfrArcsecExcludedCount);
        // The pixel-domain figures are unaffected: only the conversion was impossible.
        Assert.NotNull(detail.Hfr.Median);
    }

    [Fact]
    public void Get_OnTheSeededLibrary_SessionWithAMinorityEccentricitySource_DisclosesTheExclusion()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[3];

        var detail = Detail(library, seeded.Id, LibrarySeeder.SessionWithMinorityEccentricitySource);

        Assert.Equal(LibrarySeeder.EccentricitySourceHeader, detail.EccentricitySource);
        Assert.Equal(LibrarySeeder.FramesWithMinorityEccentricitySource, detail.EccentricityExcludedCount);
    }

    [Fact]
    public void Get_OnTheSeededLibrary_CompletesUnderABudget()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[0];

        // The rig baseline is the expensive half: a full scan of all 900 LIGHT frames. Timed
        // separately from Get so the report can say which cost what, and warmed before Get is
        // measured so the two figures do not include each other.
        var baselineWatch = Stopwatch.StartNew();
        var groups = library.Baselines.Current.Groups.Count;
        baselineWatch.Stop();
        output.WriteLine($"RigBaselinesCache first load: {baselineWatch.Elapsed.TotalMilliseconds:0.0} ms, {groups} groups");

        _ = Detail(library, seeded.Id, seeded.FirstSession);
        var best = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = Detail(library, seeded.Id, seeded.FirstSession);
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"SessionDetailQuery.Get best of 5: {best:0.0} ms");

        // Deliberately loose, matching TargetDetailQueryTests: this catches an accidental N+1 or
        // a cartesian join, not a slow build agent.
        Assert.True(best < 500d, $"Get took {best:0.0} ms");
        Assert.True(baselineWatch.Elapsed.TotalMilliseconds < 2000d);
    }
}
