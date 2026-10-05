using GalactiLog.Core.Aliases;
using GalactiLog.Core.Text;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Spec 12.16's per-filter acquisition figures and the AstroBin CSV they feed:
/// <see cref="SessionDetail.FilterAcquisitions"/> as <see cref="SessionDetailQuery"/> returns it,
/// and <see cref="AstroBinCsv.Build"/> over the whole of it.
/// </summary>
/// <remarks>
/// <para>
/// The library is the Phase 21 verification fixture's own recipe, seeded here rather than scanned:
/// three nights, 59 LIGHT frames, the same generation order and the same per-frame formulas as
/// <c>docs/superpowers/work/phase21/fixtures/GeneratePhase21Fixtures.cs</c>. The fixture library
/// under <c>C:\tmp</c> is never read, listed or written by this file; scanning it from a query
/// test would need the ingest pipeline and a private copy of 62 files per run to produce numbers
/// the recipe already states in closed form.
/// </para>
/// <para>
/// Every figure is derived from its decimal form rather than from repeated addition of 0.05, so
/// the seeded double is bit for bit the one the scanner parses out of the fixture's own
/// <c>ImageMetaData.csv</c> and <c>WeatherData.csv</c> text. A median that lands on a two-decimal
/// midpoint is decided by that double, which is why the three midpoint rows are reported
/// unrounded as well as rendered.
/// </para>
/// <para>
/// Ruling B7: <c>sky_quality</c> is filled from the weather sidecar only, never from a FITS card,
/// so the per-frame value here is the CSV formula and not the fixture's inert <c>SQM</c> keyword.
/// </para>
/// </remarks>
public class SessionDetailAcquisitionTests
{
    private static readonly DateOnly Night1 = new(2025, 1, 10);
    private static readonly DateOnly Night2 = new(2025, 2, 14);
    private static readonly DateOnly Night3 = new(2025, 3, 20);

    private const string RigA = "TestScope / TestCam";
    private const string RigB = "SecondScope / SecondCam";

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
            TargetId = LibrarySeeder.AddTarget(db.ConnectionString, "M 31").Id;
        }

        public SessionDetailQuery Query { get; }

        public Guid TargetId { get; }

        public AliasMap Aliases => _aliases.Current;

        public static Library Empty() => new(TestDatabaseFactory.CreateMigratedDatabase());

        public void Add(DateOnly night, Action<Image> frame)
            => LibrarySeeder.AddFrame(_db.ConnectionString, TargetId, night, frame);

        public SessionDetail Detail(DateOnly night)
            => Query.Get(TargetId.ToString(), night) ?? throw new InvalidOperationException("no detail");

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    // ---- the fixture recipe ------------------------------------------------------------
    // g is the fixture's global generation index, 0 to 58, and every varying value is a pure
    // function of it; the capture minute is g too, so the query reads rows in generation order.

    private static double Fwhm(int g) => (2000 + ((g % 9) * 50)) / 1000d;

    private static double SkyQuality(int g) => (2100 - ((g % 8) * 10)) / 100d;

    private static double AmbientTemp(int g) => (1000 - ((g % 12) * 50)) / 100d;

    private static void AddBlock(
        Library library,
        DateOnly night,
        string telescope,
        string camera,
        string filter,
        double exposure,
        int gain,
        int firstG,
        int count)
    {
        for (var g = firstG; g < firstG + count; g++)
        {
            var index = g;
            library.Add(night, image =>
            {
                image.Telescope = telescope;
                image.Camera = camera;
                image.FilterUsed = filter;
                image.ExposureTime = exposure;
                image.CameraGain = gain;
                image.SensorTemp = -10.00d;
                image.Fwhm = Fwhm(index);
                image.SkyQuality = SkyQuality(index);
                image.AmbientTemp = AmbientTemp(index);
                image.CaptureDate = night.ToDateTime(new TimeOnly(21, 0)).AddMinutes(index);
            });
        }
    }

    private static Library Fixture()
    {
        var library = Library.Empty();
        AddBlock(library, Night1, "TestScope", "TestCam", "Ha", 180d, 100, 0, 12);
        AddBlock(library, Night2, "TestScope", "TestCam", "OIII", 180d, 139, 12, 12);
        AddBlock(library, Night3, "TestScope", "TestCam", "Ha", 300d, 120, 24, 20);
        AddBlock(library, Night3, "SecondScope", "SecondCam", "Ha", 240d, 139, 44, 12);
        AddBlock(library, Night3, "TestScope", "TestCam", "SII", 180d, 200, 56, 3);
        return library;
    }

    private static AstroBinRow ToRow(DateOnly night, FilterAcquisition acquisition)
        => new(
            night,
            acquisition.FilterName,
            acquisition.FrameCount,
            acquisition.ExposureTime,
            acquisition.ModalGain,
            acquisition.MedianSensorTemp,
            acquisition.MedianSkyQuality,
            acquisition.MedianFwhm,
            acquisition.MedianAmbientTemp);

    // ---- cases -------------------------------------------------------------------------

    /// <summary>Case 1. The acquisitions align with the detail rows index for index. A second
    /// grouping rule would put one filter's figures on another filter's row, and nothing on the
    /// page would say so.</summary>
    [Fact]
    public void FilterAcquisitions_AlignWithFilterDetails()
    {
        using var library = Fixture();

        var detail = library.Detail(Night3);
        var acquisitions = detail.FilterAcquisitions;

        Assert.NotNull(acquisitions);
        Assert.Equal(detail.FilterDetails.Count, acquisitions.Count);
        for (var i = 0; i < acquisitions.Count; i++)
        {
            Assert.Equal(detail.FilterDetails[i].FilterName, acquisitions[i].FilterName);
            Assert.Equal(detail.FilterDetails[i].FrameCount, acquisitions[i].FrameCount);
            Assert.Equal(detail.FilterDetails[i].ExposureTime, acquisitions[i].ExposureTime);
            Assert.Equal(detail.FilterDetails[i].RigLabel, acquisitions[i].RigLabel);
        }
    }

    /// <summary>Case 3. The night the two rigs share yields three rows, two for rig A in filter
    /// order and one for rig B, each rig's gain its own. A split that merged the rigs, which the
    /// web's own grouping does, would put one gain on frames that never carried it.</summary>
    [Fact]
    public void FilterAcquisitions_SplitTheTwoRigsOfOneNight()
    {
        using var library = Fixture();

        var acquisitions = library.Detail(Night3).FilterAcquisitions!;

        Assert.Collection(
            acquisitions,
            row =>
            {
                Assert.Equal("Ha", row.FilterName);
                Assert.Equal(RigA, row.RigLabel);
                Assert.Equal(20, row.FrameCount);
                Assert.Equal(300d, row.ExposureTime);
                Assert.Equal(120, row.ModalGain);
            },
            row =>
            {
                Assert.Equal("SII", row.FilterName);
                Assert.Equal(RigA, row.RigLabel);
                Assert.Equal(3, row.FrameCount);
                Assert.Equal(200, row.ModalGain);
            },
            row =>
            {
                Assert.Equal("Ha", row.FilterName);
                Assert.Equal(RigB, row.RigLabel);
                Assert.Equal(12, row.FrameCount);
                Assert.Equal(240d, row.ExposureTime);
                Assert.Equal(139, row.ModalGain);
            });
    }

    /// <summary>Case 2. The whole file over the fixture's three nights, newest night first: the
    /// header and five lines. Any cell rule, the row order, the rig split or the trailing newline
    /// moves this text.</summary>
    [Fact]
    public void Build_RendersTheFiveGoldenRows()
    {
        using var library = Fixture();

        List<AstroBinRow> rows = [];
        foreach (var night in new[] { Night3, Night2, Night1 })
        {
            rows.AddRange(library.Detail(night).FilterAcquisitions!.Select(row => ToRow(night, row)));
        }

        var csv = AstroBinCsv.Build(
            rows,
            new Dictionary<string, int> { ["Ha"] = 1, ["OIII"] = 2, ["SII"] = 3 },
            4,
            library.Aliases);

        string[] expected =
        [
            AstroBinCsv.Header,
            "2025-03-20,1,20,300,,120,-10,,4,20.70,2.23,7.75",
            "2025-03-20,3,3,180,,200,-10,,4,20.90,2.15,5.50",
            "2025-03-20,1,12,240,,139,-10,,4,20.55,2.17,7.25",
            "2025-02-14,2,12,180,,139,-10,,4,20.55,2.20,7.25",
            "2025-01-10,1,12,180,,100,-10,,4,20.75,2.13,7.25",
        ];
        Assert.Equal(string.Join("\n", expected) + "\n", csv);
    }

    /// <summary>The three medians that land on a two-decimal midpoint, pinned unrounded (ruling
    /// B6). The rendered cell follows the double and not the decimal it prints as: 2.175 is held
    /// as a value just below the midpoint, so it rounds down while the other two round up. A
    /// change to the seeded values or to <c>Statistics.Median</c> moves these.</summary>
    [Fact]
    public void FilterAcquisitions_HoldTheMidpointFwhmMediansAsDoubles()
    {
        using var library = Fixture();

        var night3 = library.Detail(Night3).FilterAcquisitions!;
        var night1 = library.Detail(Night1).FilterAcquisitions!;

        Assert.Equal(2.225d, night3[0].MedianFwhm);
        Assert.Equal(2.175d, night3[2].MedianFwhm);
        Assert.Equal(2.125d, night1[0].MedianFwhm);

        // The doubles themselves, which is what the rounding reads: 2.225 and 2.125 sit at or
        // above their midpoint and 2.175 sits below it.
        Assert.True(night3[0].MedianFwhm!.Value * 100d >= 222.5d);
        Assert.True(night3[2].MedianFwhm!.Value * 100d < 217.5d);
        Assert.True(night1[0].MedianFwhm!.Value * 100d >= 212.5d);
    }

    /// <summary>A construction site that omits <c>FilterAcquisitions</c> gets an empty list, never
    /// null, so a night the page has not computed acquisitions for cannot be misread as one whose
    /// export failed.</summary>
    [Fact]
    public void FilterAcquisitions_DefaultsToAnEmptyListNotNull()
    {
        var empty = new MetricRangeSummary(null, null, null);
        var detail = new SessionDetail(
            "obj:1", Night1, 0, 0d, empty, null, 0, empty, null, 0, empty, empty, empty, null,
            [], null, null, [], [], null, null, null, [], [], null);

        Assert.NotNull(detail.FilterAcquisitions);
        Assert.Empty(detail.FilterAcquisitions);
    }

    /// <summary>Case 4, ruling B8. The modal gain on hand-built frames: a majority wins, a tie is
    /// broken by the smaller value, and a group in which no frame carries a gain renders an empty
    /// cell. Every group of the fixture library is gain-constant, so a group-and-take-the-first
    /// passes there and fails only here.</summary>
    [Theory]
    [InlineData(3, 2, 120)]
    [InlineData(2, 3, 139)]
    [InlineData(2, 2, 120)]
    public void ModalGain_TakesTheMajorityAndBreaksATieOnTheSmallerValue(int at120, int at139, int expected)
    {
        using var library = Library.Empty();
        var minute = 0;

        // The higher gain is written first, so a rule that returns the first group's value rather
        // than the majority answers 139 in every one of the three cases.
        for (var i = 0; i < at139; i++)
        {
            AddGainFrame(library, 139, minute++);
        }

        for (var i = 0; i < at120; i++)
        {
            AddGainFrame(library, 120, minute++);
        }

        Assert.Equal(expected, library.Detail(Night1).FilterAcquisitions![0].ModalGain);
    }

    /// <summary>Case 4's third part: no frame of the group carries a gain, so the figure is null
    /// and the cell is blank. A zero would be a measurement.</summary>
    [Fact]
    public void ModalGain_IsNullAndTheCellBlankWhenNoFrameCarriesOne()
    {
        using var library = Library.Empty();
        AddGainFrame(library, null, 0);
        AddGainFrame(library, null, 1);

        var acquisition = library.Detail(Night1).FilterAcquisitions![0];
        Assert.Null(acquisition.ModalGain);

        var csv = AstroBinCsv.Build(
            [ToRow(Night1, acquisition)], new Dictionary<string, int>(), null, library.Aliases);
        Assert.Equal(string.Empty, csv.Split('\n')[1].Split(',')[5]);
    }

    private static void AddGainFrame(Library library, int? gain, int minute)
        => library.Add(Night1, image =>
        {
            image.Telescope = "TestScope";
            image.Camera = "TestCam";
            image.FilterUsed = "Ha";
            image.ExposureTime = 180d;
            image.CameraGain = gain;
            image.CaptureDate = Night1.ToDateTime(new TimeOnly(21, 0)).AddMinutes(minute);
        });
}
