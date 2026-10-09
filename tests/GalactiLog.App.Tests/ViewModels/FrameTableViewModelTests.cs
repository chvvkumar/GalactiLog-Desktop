using System.Diagnostics;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.4 and 18.3: the frame table owns the 32 columns, their persisted visibility, the
// metric-group gate over that visibility, sorting in both directions on every column, and the five
// per-frame actions. Plain xunit facts: no window, no dispatcher, no database, and no filesystem.
// The frames are handed in, exactly as the session card hands them in.
public class FrameTableViewModelTests
{
    internal const string Rig = "RC8 / ASI2600MM";

    /// <summary>A second rig, for P14A's multi-rig cases (PAR-004). It sorts before
    /// <see cref="Rig"/> alphabetically, so a case that hands it in second proves the order is
    /// first capture rather than alphabetical.</summary>
    internal const string SecondRig = "FRA600 / ASI294MC";

    // ---- construction helpers -------------------------------------------------------------

    /// <summary>One frame row. Everything is optional so a test names only the fields it asserts,
    /// and the ones it does not name stay null, which is what "no frame carried this metric"
    /// looks like in the read model.</summary>
    internal static FrameRow Frame(
        Guid? imageId = null,
        string filePath = @"D:\Astro\M31\frame.fits",
        string fileName = "frame.fits",
        DateTime? captureDate = null,
        string? filterUsed = null,
        double? exposureTime = null,
        double? medianHfr = null,
        double? eccentricity = null,
        double? fwhm = null,
        int? detectedStars = null,
        double? guidingRmsArcsec = null,
        double? guidingRmsRaArcsec = null,
        double? guidingRmsDecArcsec = null,
        string? guidingRmsSource = null,
        double? aduMean = null,
        double? aduMedian = null,
        double? aduStdev = null,
        int? aduMin = null,
        int? aduMax = null,
        int? focuserPosition = null,
        double? focuserTemp = null,
        double? ambientTemp = null,
        double? dewPoint = null,
        double? humidity = null,
        double? pressure = null,
        double? windSpeed = null,
        double? windDirection = null,
        double? windGust = null,
        double? cloudCover = null,
        double? skyQuality = null,
        double? airmass = null,
        string? pierSide = null,
        double? rotatorPosition = null,
        double? sensorTemp = null,
        int? cameraGain = null,
        bool isHfrOutlier = false,
        bool isEccentricityOutlier = false,
        string? rig = null,
        bool isFwhmOutlier = false,
        bool isStarsOutlier = false,
        bool isGuidingRmsOutlier = false)
        => new(
            imageId ?? Guid.NewGuid(), filePath, fileName, captureDate,
            filterUsed, exposureTime,
            medianHfr, eccentricity, fwhm, detectedStars,
            guidingRmsArcsec, guidingRmsRaArcsec, guidingRmsDecArcsec, guidingRmsSource,
            aduMean, aduMedian, aduStdev, aduMin, aduMax,
            focuserPosition, focuserTemp,
            ambientTemp, dewPoint, humidity, pressure,
            windSpeed, windDirection, windGust, cloudCover, skyQuality,
            airmass, pierSide, rotatorPosition,
            sensorTemp, cameraGain,
            rig ?? Rig,
            isHfrOutlier, isEccentricityOutlier, isFwhmOutlier, isStarsOutlier, isGuidingRmsOutlier);

    /// <summary>Every metric group enabled, so a test about the persisted list is not silently
    /// also a test about the gate. The shipped document leaves four groups off.</summary>
    private static DisplaySettings EveryGroup(string[]? frameColumns = null)
    {
        var display = new DisplaySettings();
        foreach (var key in display.Groups.Keys.ToList())
        {
            display.Groups[key] = display.Groups[key] with { Enabled = true };
        }

        if (frameColumns is not null)
        {
            display.Columns[DisplaySettings.FramesTableId] = frameColumns;
        }

        return display;
    }

    private sealed class Harness
    {
        public List<ProcessStartInfo> Launches { get; } = [];

        public List<string> Copies { get; } = [];

        /// <summary>The row the modal was opened on, so the Phase 6 assertion reads unchanged
        /// after Phase 8 Task 7 widened the seam to a list plus an index.</summary>
        public List<FrameRowViewModel> Previews { get; } = [];

        /// <summary>What Phase 8 Task 7's seam actually hands over: the table's rows in their
        /// current sort order, and the clicked row's index within them.</summary>
        public List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)> PreviewCalls { get; } = [];

        /// <summary>Every image id <c>ToggleRawHeadersCommand</c> asked for headers. Review
        /// finding 7's once-per-row contract at the seam: expanding, collapsing and re-expanding
        /// the same row must add exactly one entry, not two.</summary>
        public List<Guid> HeaderQueries { get; } = [];

        public DisplaySettings Display { get; set; } = new();

        public int Saves { get; private set; }

        public string[] FrameColumns => Display.ColumnsFor(DisplaySettings.FramesTableId);

        public DisplayColumnWriter Columns { get; private set; } = null!;

        public ShellIntegration Shell { get; private set; } = null!;

        public FrameTableViewModel Table { get; private set; } = null!;

        /// <summary>A second table on the same writer, the way one expanded session card is a
        /// second table on the page. The display document it is handed is the caller's, because
        /// AppHost hands every table the same startup snapshot (ruling Q13).</summary>
        public FrameTableViewModel NewTable(DisplaySettings display, IReadOnlyList<FrameRow>? frames = null)
            => new(
                frames ?? [],
                display,
                Columns,
                Shell,
                openPreview: null,
                new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                // Phase 6 Task 6: no test in this file asserts raw-header behaviour, so the query
                // delegate is a stub that is never expected to be called.
                getHeaders: _ => null);

        public Task Pending => Columns.Pending;

        public static Harness Create(
            IReadOnlyList<FrameRow>? frames = null,
            DisplaySettings? display = null,
            GeneralSettings? general = null,
            bool withPreview = false,
            bool datePrefixed = false)
        {
            var harness = new Harness { Display = display ?? new DisplaySettings() };

            harness.Columns = new DisplayColumnWriter(
                () => harness.Display,
                value =>
                {
                    harness.Display = value;
                    harness.Saves++;
                });

            harness.Shell = new ShellIntegration(
                copyText: text =>
                {
                    harness.Copies.Add(text);
                    return Task.CompletedTask;
                },
                start: info =>
                {
                    harness.Launches.Add(info);
                    return null;
                });

            harness.Table = new FrameTableViewModel(
                frames ?? [],
                harness.Display,
                harness.Columns,
                harness.Shell,
                withPreview
                    ? (rows, index) =>
                    {
                        harness.PreviewCalls.Add((rows, index));
                        harness.Previews.Add(rows[index]);
                    }
                : null,
                general ?? new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                // Phase 6 Task 6: ToggleRawHeadersCommand does call this on a row's first
                // expansion (review finding 7), so it is a counting stub rather than a dead one;
                // it still returns null, since no test in this file asserts rendered header
                // content, only the once-per-row call contract.
                getHeaders: id =>
                {
                    harness.HeaderQueries.Add(id);
                    return null;
                },
                datePrefixed: datePrefixed);

            return harness;
        }
    }

    // ---- columns --------------------------------------------------------------------------

    [Fact]
    public void Columns_LoadFromDisplayColumnsFrames()
    {
        var harness = Harness.Create(display: EveryGroup(["time", "fwhm", "airmass"]));

        // All 32 exist whatever the persisted list says; the rendered subset is the stored one, in
        // spec 12.4's order rather than the order the keys appear in the document.
        Assert.Equal(32, harness.Table.Columns.Count);
        Assert.Equal(
            ["time", "fwhm", "airmass"],
            harness.Table.VisibleColumns.Select(column => column.Key));
    }

    [Fact]
    public void Columns_MissingFramesEntry_FallsBackToTheDocumentedDefault()
    {
        // Spec 5.8.2: "A table id absent from the map uses that table's default list". Phase 5's
        // ColumnsFor doing its job unchanged.
        var display = EveryGroup();
        display.Columns.Remove(DisplaySettings.FramesTableId);

        var harness = Harness.Create(display: display);

        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.FramesTableId],
            harness.Table.VisibleColumns.Select(column => column.Key));
    }

    [Fact]
    public void Columns_EmptyFramesArray_IsLegalAndRendersNoColumns()
    {
        // Ruling Q15: an empty columns.frames array is a legal state that must not throw. The
        // table renders its header row and empty rows, and the picker is how the user gets out.
        var harness = Harness.Create(frames: [Frame()], display: EveryGroup([]));

        Assert.Empty(harness.Table.VisibleColumns);
        Assert.Single(harness.Table.Rows);
        Assert.All(harness.Table.Columns, column => Assert.True(column.CanHide));
    }

    [Fact]
    public void Columns_DisabledGroup_HidesItsColumnsEvenWhenPersistedAsVisible()
    {
        // The roadmap Verify line. The persisted list names the RMS column, the guiding group is
        // off, and the column does not render: spec 5.8.2's "the group toggle wins".
        var display = EveryGroup(["time", "guiding_rms_arcsec", "fwhm"]);
        display.Groups["guiding"] = display.Groups["guiding"] with { Enabled = false };

        var harness = Harness.Create(display: display);
        var rms = harness.Table.Columns.Single(column => column.Key == "guiding_rms_arcsec");

        Assert.DoesNotContain(
            "guiding_rms_arcsec",
            harness.Table.VisibleColumns.Select(column => column.Key));

        // Hidden, but still in the persisted list and still marked visible, so turning the group
        // back on in Settings brings the user's column back rather than a default one.
        Assert.True(rms.IsVisible);
        Assert.False(rms.IsGroupEnabled);
        Assert.False(rms.IsShown);

        // Every other RMS column goes with it, and nothing outside the group moves.
        Assert.All(
            harness.Table.Columns.Where(column => column.Key.StartsWith("guiding_rms", StringComparison.Ordinal)),
            column => Assert.False(column.IsGroupEnabled));
        Assert.Contains("fwhm", harness.Table.VisibleColumns.Select(column => column.Key));
    }

    [Fact]
    public void Columns_DisabledField_HidesItsColumn()
    {
        // The group is on and one field inside it is off: the same gate, one column deep.
        var display = EveryGroup(["median_hfr", "fwhm"]);
        display.Groups["quality"] = new MetricGroupSettings(
            true,
            new Dictionary<string, bool>(display.Groups["quality"].Fields) { ["fwhm"] = false });

        var harness = Harness.Create(display: display);

        Assert.Equal(["median_hfr"], harness.Table.VisibleColumns.Select(column => column.Key));
    }

    [Fact]
    public async Task Columns_ToggleWritesTheOrderedVisibleKeyList()
    {
        var harness = Harness.Create(display: EveryGroup(["time", "file_name", "fwhm"]));

        harness.Table.ToggleColumnCommand.Execute(
            harness.Table.Columns.Single(column => column.Key == "airmass"));
        await harness.Pending;

        // Spec 12.4's column order, not the order the user clicked: airmass sorts into its
        // documented place rather than onto the end.
        Assert.Equal(["time", "file_name", "fwhm", "airmass"], harness.FrameColumns);
        Assert.Equal(1, harness.Saves);

        // And back off again.
        harness.Table.ToggleColumnCommand.Execute(
            harness.Table.Columns.Single(column => column.Key == "fwhm"));
        await harness.Pending;

        Assert.Equal(["time", "file_name", "airmass"], harness.FrameColumns);
    }

    [Fact]
    public async Task Columns_ToggleOfAGatedColumn_IsRefused()
    {
        // Turning a gated column on would change nothing visible and would teach the wrong model
        // of why the column is missing. The picker disables it and says where the group lives.
        var display = EveryGroup(["time"]);
        display.Groups["mount"] = display.Groups["mount"] with { Enabled = false };

        var harness = Harness.Create(display: display);
        var airmass = harness.Table.Columns.Single(column => column.Key == "airmass");

        harness.Table.ToggleColumnCommand.Execute(airmass);
        await harness.Pending;

        Assert.False(airmass.IsVisible);
        Assert.Equal(0, harness.Saves);
        Assert.Equal(["time"], harness.FrameColumns);
    }

    [Fact]
    public async Task Columns_ToggleDoesNotReorderTheList()
    {
        // Hide the last, then the first, then turn the last back on. Click order is
        // detected_stars, time, detected_stars; the persisted list is still spec 12.4's order.
        var harness = Harness.Create(display: EveryGroup());

        foreach (var key in new[] { "detected_stars", "time", "detected_stars" })
        {
            harness.Table.ToggleColumnCommand.Execute(
                harness.Table.Columns.Single(column => column.Key == key));
        }

        await harness.Pending;

        Assert.Equal(
            ["file_name", "filter_used", "exposure_time", "median_hfr", "eccentricity", "fwhm", "detected_stars"],
            harness.FrameColumns);
    }

    [Fact]
    public async Task Columns_ToggleOfAVisibleColumn_KeepsGatedColumnsInThePersistedList()
    {
        // The trap in persisting the rendered subset instead of the visible list: with a group
        // off, one unrelated toggle would delete every gated column from the document, and the
        // user's columns would not come back when the group did.
        var display = new DisplaySettings
        {
            Columns = new Dictionary<string, string[]>
            {
                [DisplaySettings.FramesTableId] = ["time", "airmass", "fwhm"],
            },
        };

        var harness = Harness.Create(display: display);
        Assert.False(harness.Table.Columns.Single(column => column.Key == "airmass").IsGroupEnabled);

        harness.Table.ToggleColumnCommand.Execute(
            harness.Table.Columns.Single(column => column.Key == "fwhm"));
        await harness.Pending;

        Assert.Equal(["time", "airmass"], harness.FrameColumns);
    }

    [Fact]
    public async Task Columns_ASecondTable_StartsFromWhatTheWriterLastWrote()
    {
        // Review finding 1. AppHost reads the display document once, on Program.Main's thread
        // (ruling Q13), and hands that same value to every frame table, so the snapshot is stale
        // the moment one table writes. Without the writer's memory, expanding a second session
        // card showed the column the user had just hidden, and that table's own first toggle
        // rewrote the list from the snapshot and silently put the hidden column back.
        var snapshot = EveryGroup(["time", "file_name", "fwhm"]);
        var harness = Harness.Create(display: snapshot);

        harness.Table.ToggleColumnCommand.Execute(
            harness.Table.Columns.Single(column => column.Key == "fwhm"));
        await harness.Pending;
        Assert.Equal(["time", "file_name"], harness.FrameColumns);

        // The snapshot still lists fwhm. The table built from it must not.
        Assert.Contains("fwhm", snapshot.ColumnsFor(DisplaySettings.FramesTableId));
        var second = harness.NewTable(snapshot);
        Assert.Equal(["time", "file_name"], second.VisibleColumns.Select(column => column.Key));

        // And its first toggle extends the written list rather than reverting the hide.
        second.ToggleColumnCommand.Execute(
            second.Columns.Single(column => column.Key == "airmass"));
        await harness.Pending;

        Assert.Equal(["time", "file_name", "airmass"], harness.FrameColumns);
        Assert.False(harness.Table.Columns.Single(column => column.Key == "fwhm").IsVisible);
    }

    [Fact]
    public async Task Columns_TwoLiveTables_EachAppliesTheOthersToggle()
    {
        // Phase review item 1. LastWritten only covers a table built after the write; two cards
        // expanded at the same time are two live tables, and the one that was built first kept the
        // list it was built with. Its next toggle then rewrote the document from that stale list
        // and reverted the other table's hide.
        var snapshot = EveryGroup(["time", "file_name", "fwhm", "airmass"]);
        var harness = Harness.Create(display: snapshot);
        var second = harness.NewTable(snapshot);

        // Table B hides fwhm while table A is already on screen.
        second.ToggleColumnCommand.Execute(second.Columns.Single(column => column.Key == "fwhm"));
        await harness.Pending;
        Assert.Equal(["time", "file_name", "airmass"], harness.FrameColumns);

        // Table A learned of it, so the column stops rendering there too and its own next toggle
        // extends the written list instead of resurrecting fwhm.
        Assert.False(harness.Table.Columns.Single(column => column.Key == "fwhm").IsVisible);
        Assert.DoesNotContain("fwhm", harness.Table.VisibleColumns.Select(column => column.Key));

        harness.Table.ToggleColumnCommand.Execute(
            harness.Table.Columns.Single(column => column.Key == "sensor_temp"));
        await harness.Pending;

        Assert.Equal(["time", "file_name", "airmass", "sensor_temp"], harness.FrameColumns);
        Assert.False(second.Columns.Single(column => column.Key == "fwhm").IsVisible);
        Assert.True(second.Columns.Single(column => column.Key == "sensor_temp").IsVisible);
    }

    [Fact]
    public async Task Columns_ADisposedTable_StopsFollowingTheWriter()
    {
        // The card disposes its frame table when it collapses (SessionCardViewModel.DisposeChildren
        // casts both children to IDisposable), so a table that is off screen must not stay
        // reachable from the process-wide writer.
        var snapshot = EveryGroup(["time", "file_name", "fwhm"]);
        var harness = Harness.Create(display: snapshot);
        var second = harness.NewTable(snapshot);

        second.Dispose();

        harness.Table.ToggleColumnCommand.Execute(
            harness.Table.Columns.Single(column => column.Key == "fwhm"));
        await harness.Pending;

        Assert.True(second.Columns.Single(column => column.Key == "fwhm").IsVisible);
    }

    // ---- sorting --------------------------------------------------------------------------

    // Three frames whose every sortable field ascends with the frame's position, so a correct
    // ascending sort on any of the 32 keys is the order they were handed in and a correct
    // descending sort is its reverse. One fixture, 32 columns, no per-column expectation to keep
    // in step with the read model.
    private static IReadOnlyList<FrameRow> Ascending()
        => [.. Enumerable.Range(1, 3).Select(step => Frame(
            filePath: $@"D:\Astro\M31\{(char)('a' + step - 1)}.fits",
            fileName: $"{(char)('a' + step - 1)}.fits",
            captureDate: new DateTime(2025, 12, 7, 20, step, 0, DateTimeKind.Utc),
            filterUsed: new[] { "Ha", "Lum", "OIII" }[step - 1],
            exposureTime: step,
            medianHfr: step,
            eccentricity: step,
            fwhm: step,
            detectedStars: step,
            guidingRmsArcsec: step,
            guidingRmsRaArcsec: step,
            guidingRmsDecArcsec: step,
            aduMean: step,
            aduMedian: step,
            aduStdev: step,
            aduMin: step,
            aduMax: step,
            focuserPosition: step,
            focuserTemp: step,
            ambientTemp: step,
            dewPoint: step,
            humidity: step,
            pressure: step,
            windSpeed: step,
            windDirection: step,
            windGust: step,
            cloudCover: step,
            skyQuality: step,
            airmass: step,
            pierSide: new[] { "East", "North", "West" }[step - 1],
            rotatorPosition: step,
            sensorTemp: step,
            cameraGain: step))];

    private static readonly DateTime BaseCapture = new(2025, 12, 7, 20, 0, 0, DateTimeKind.Utc);

    // Every field the same in all three frames. A selector that reads the wrong field therefore
    // sees three equal keys, and a stable sort hands back the input order in both directions,
    // which is what makes the descending assertion below a real check on which field a column
    // reads (review finding 4).
    private static FrameRow Constant() => Frame(
        fileName: "same.fits",
        captureDate: BaseCapture,
        filterUsed: "Ha",
        exposureTime: 300d,
        medianHfr: 2d,
        eccentricity: 0.4d,
        fwhm: 1.9d,
        detectedStars: 1000,
        guidingRmsArcsec: 0.4d,
        guidingRmsRaArcsec: 0.3d,
        guidingRmsDecArcsec: 0.3d,
        aduMean: 1000d,
        aduMedian: 1000d,
        aduStdev: 50d,
        aduMin: 100,
        aduMax: 60_000,
        focuserPosition: 24_000,
        focuserTemp: 4d,
        ambientTemp: 3d,
        dewPoint: 1d,
        humidity: 60d,
        pressure: 1013d,
        windSpeed: 3d,
        windDirection: 270d,
        windGust: 6d,
        cloudCover: 10d,
        skyQuality: 21d,
        airmass: 1.2d,
        pierSide: "East",
        rotatorPosition: 90d,
        sensorTemp: -10d,
        cameraGain: 100);

    // One field of an otherwise constant frame, ascending with step. The arms are the read-model
    // fields spec 12.4's table names, one per column key, which is the mapping the view-model's
    // sort table also has to get right.
    private static FrameRow Vary(string key, int step)
    {
        var frame = Constant();
        return key switch
        {
            "time" => frame with { CaptureDate = BaseCapture.AddMinutes(step) },
            "file_name" => frame with { FileName = $"{(char)('a' + step)}.fits" },
            "filter_used" => frame with { FilterUsed = new[] { "Aa", "Bb", "Cc" }[step] },
            "exposure_time" => frame with { ExposureTime = 300d + step },
            "median_hfr" => frame with { MedianHfr = 2d + step },
            "eccentricity" => frame with { Eccentricity = 0.4d + step },
            "fwhm" => frame with { Fwhm = 1.9d + step },
            "detected_stars" => frame with { DetectedStars = 1000 + step },
            "guiding_rms_arcsec" => frame with { GuidingRmsArcsec = 0.4d + step },
            "guiding_rms_ra_arcsec" => frame with { GuidingRmsRaArcsec = 0.3d + step },
            "guiding_rms_dec_arcsec" => frame with { GuidingRmsDecArcsec = 0.3d + step },
            "adu_mean" => frame with { AduMean = 1000d + step },
            "adu_median" => frame with { AduMedian = 1000d + step },
            "adu_stdev" => frame with { AduStdev = 50d + step },
            "adu_min" => frame with { AduMin = 100 + step },
            "adu_max" => frame with { AduMax = 60_000 + step },
            "focuser_position" => frame with { FocuserPosition = 24_000 + step },
            "focuser_temp" => frame with { FocuserTemp = 4d + step },
            "ambient_temp" => frame with { AmbientTemp = 3d + step },
            "dew_point" => frame with { DewPoint = 1d + step },
            "humidity" => frame with { Humidity = 60d + step },
            "pressure" => frame with { Pressure = 1013d + step },
            "wind_speed" => frame with { WindSpeed = 3d + step },
            "wind_direction" => frame with { WindDirection = 270d + step },
            "wind_gust" => frame with { WindGust = 6d + step },
            "cloud_cover" => frame with { CloudCover = 10d + step },
            "sky_quality" => frame with { SkyQuality = 21d + step },
            "airmass" => frame with { Airmass = 1.2d + step },
            "pier_side" => frame with { PierSide = new[] { "East", "North", "West" }[step] },
            "rotator_position" => frame with { RotatorPosition = 90d + step },
            "sensor_temp" => frame with { SensorTemp = -10d + step },
            "camera_gain" => frame with { CameraGain = 100 + step },
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No field is varied for this column key."),
        };
    }

    public static TheoryData<string> EveryColumnKey()
    {
        var data = new TheoryData<string>();
        foreach (var column in Core.Settings.FrameColumns.All)
        {
            data.Add(column.Key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryColumnKey))]
    public void Sort_EveryColumnKeySortsBothDirections(string columnKey)
    {
        // The roadmap Verify line. Spec 12.4: "Every column is sortable ascending and descending",
        // which includes the four always-on columns and the three textual ones.
        var harness = Harness.Create(frames: Ascending(), display: EveryGroup());

        harness.Table.SortByCommand.Execute(columnKey);

        Assert.Equal(columnKey, harness.Table.SortKey);
        Assert.False(harness.Table.Descending);
        Assert.Equal(
            ["a.fits", "b.fits", "c.fits"],
            harness.Table.Rows.Select(row => row.FileName));

        harness.Table.SortByCommand.Execute(columnKey);

        Assert.True(harness.Table.Descending);
        Assert.Equal(
            ["c.fits", "b.fits", "a.fits"],
            harness.Table.Rows.Select(row => row.FileName));
    }

    [Theory]
    [MemberData(nameof(EveryColumnKey))]
    public void Sort_EachColumnKeyReadsItsOwnField(string columnKey)
    {
        // Review finding 4. The other sort theory varies every field together, so a selector
        // wired to the wrong read-model field still produces the right order. Here only this
        // column's field varies: a wrong selector sees three equal keys, the stable sort keeps the
        // input order, and the descending assertion is what catches it.
        IReadOnlyList<FrameRow> frames = [.. Enumerable.Range(0, 3).Select(step => Vary(columnKey, step))];
        var harness = Harness.Create(frames: frames, display: EveryGroup());

        harness.Table.SortByCommand.Execute(columnKey);
        Assert.Equal(frames, harness.Table.Rows.Select(row => row.Row));

        harness.Table.SortByCommand.Execute(columnKey);
        Assert.Equal(frames.Reverse(), harness.Table.Rows.Select(row => row.Row));
    }

    [Fact]
    public void Sort_RepeatClick_FlipsDirection()
    {
        var harness = Harness.Create(frames: Ascending(), display: EveryGroup());

        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.False(harness.Table.Descending);

        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.True(harness.Table.Descending);

        // A different column starts ascending again rather than inheriting the direction.
        harness.Table.SortByCommand.Execute("fwhm");
        Assert.Equal("fwhm", harness.Table.SortKey);
        Assert.False(harness.Table.Descending);

        // The header glyph follows the active column and only the active column.
        Assert.Equal("\u25B2", harness.Table.Columns.Single(column => column.Key == "fwhm").SortGlyph);
        Assert.Equal("", harness.Table.Columns.Single(column => column.Key == "median_hfr").SortGlyph);
    }

    [Fact]
    public void Sort_NullsSortLastInBothDirections()
    {
        // Flipping the direction must not march a block of unmeasured frames through the middle
        // of the list.
        IReadOnlyList<FrameRow> frames =
        [
            Frame(fileName: "measured-high.fits", medianHfr: 3.0d, captureDate: new DateTime(2025, 12, 7, 20, 1, 0, DateTimeKind.Utc)),
            Frame(fileName: "unmeasured.fits", captureDate: new DateTime(2025, 12, 7, 20, 2, 0, DateTimeKind.Utc)),
            Frame(fileName: "measured-low.fits", medianHfr: 1.0d, captureDate: new DateTime(2025, 12, 7, 20, 3, 0, DateTimeKind.Utc)),
        ];

        var harness = Harness.Create(frames: frames, display: EveryGroup());

        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.Equal(
            ["measured-low.fits", "measured-high.fits", "unmeasured.fits"],
            harness.Table.Rows.Select(row => row.FileName));

        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.Equal(
            ["measured-high.fits", "measured-low.fits", "unmeasured.fits"],
            harness.Table.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void Sort_TiesBreakOnCaptureDateThenFileName()
    {
        // The order has to be total, or a re-sort of the same key can reshuffle equal rows.
        IReadOnlyList<FrameRow> frames =
        [
            Frame(fileName: "late.fits", medianHfr: 2.0d, captureDate: new DateTime(2025, 12, 7, 22, 0, 0, DateTimeKind.Utc)),
            Frame(fileName: "b-early.fits", medianHfr: 2.0d, captureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc)),
            Frame(fileName: "a-early.fits", medianHfr: 2.0d, captureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc)),
        ];

        var harness = Harness.Create(frames: frames, display: EveryGroup());

        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.Equal(
            ["a-early.fits", "b-early.fits", "late.fits"],
            harness.Table.Rows.Select(row => row.FileName));

        // The tie-break does not flip with the direction: only the sort key does.
        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.Equal(
            ["a-early.fits", "b-early.fits", "late.fits"],
            harness.Table.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void Sort_UnknownColumnKey_IsIgnored()
    {
        var harness = Harness.Create(frames: Ascending(), display: EveryGroup());

        harness.Table.SortByCommand.Execute("median_fwhm");
        harness.Table.SortByCommand.Execute(null);

        Assert.Equal("", harness.Table.SortKey);
        Assert.Equal(
            ["a.fits", "b.fits", "c.fits"],
            harness.Table.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void Rows_AreInCaptureOrderBeforeTheFirstSort()
    {
        // The query already returns capture order (Task 2 handoff); the table does not reorder on
        // construction, so an unsorted table is the night as it happened.
        IReadOnlyList<FrameRow> frames =
        [
            Frame(fileName: "z.fits", captureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc)),
            Frame(fileName: "a.fits", captureDate: new DateTime(2025, 12, 7, 22, 0, 0, DateTimeKind.Utc)),
        ];

        var harness = Harness.Create(frames: frames, display: EveryGroup());

        Assert.Equal(["z.fits", "a.fits"], harness.Table.Rows.Select(row => row.FileName));
    }

    // ---- cells ----------------------------------------------------------------------------

    [Fact]
    public void Rows_FormatEveryColumnValue()
    {
        var frame = Frame(
            filePath: @"D:\Astro\M31\M31_Ha_001.fits",
            fileName: "M31_Ha_001.fits",
            captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: 2.345d,
            eccentricity: 0.412d,
            fwhm: 1.876d,
            detectedStars: 1490,
            guidingRmsArcsec: 0.452d,
            guidingRmsRaArcsec: 0.311d,
            guidingRmsDecArcsec: 0.327d,
            guidingRmsSource: "phd2",
            aduMean: 1234.6d,
            aduMedian: 1200.4d,
            aduStdev: 88.7d,
            aduMin: 900,
            aduMax: 65000,
            focuserPosition: 24500,
            focuserTemp: 4.55d,
            ambientTemp: 3.44d,
            dewPoint: 1.55d,
            humidity: 62.4d,
            pressure: 1013.4d,
            windSpeed: 3.44d,
            windDirection: 271.4d,
            windGust: 6.55d,
            cloudCover: 12.4d,
            skyQuality: 21.345d,
            airmass: 1.234d,
            pierSide: "East",
            rotatorPosition: 91.44d,
            sensorTemp: -10.44d,
            cameraGain: 100);

        var row = Harness.Create(frames: [frame], display: EveryGroup()).Table.Rows[0];

        Assert.Equal("21:05", row.TimeText);
        Assert.Equal("M31_Ha_001.fits", row.FileName);
        Assert.Equal("Ha", row.FilterText);
        // P12 Task 6: the unit moved from the cell to the header ("Exp s").
        Assert.Equal("300", row.ExposureText);
        Assert.Equal("2.35", row.MedianHfrText);
        Assert.Equal("0.41", row.EccentricityText);
        Assert.Equal("1.88", row.FwhmText);
        Assert.Equal("1,490", row.DetectedStarsText);
        Assert.Equal("0.45", row.GuidingRmsText);
        Assert.Equal("0.31", row.GuidingRmsRaText);
        Assert.Equal("0.33", row.GuidingRmsDecText);
        Assert.Equal("1,235", row.AduMeanText);
        Assert.Equal("1,200", row.AduMedianText);
        Assert.Equal("89", row.AduStdevText);
        Assert.Equal("900", row.AduMinText);
        Assert.Equal("65,000", row.AduMaxText);
        Assert.Equal("24,500", row.FocuserPositionText);
        Assert.Equal("4.6", row.FocuserTempText);
        Assert.Equal("3.4", row.AmbientTempText);
        Assert.Equal("1.6", row.DewPointText);
        Assert.Equal("62", row.HumidityText);
        Assert.Equal("1013", row.PressureText);
        Assert.Equal("3.4", row.WindSpeedText);
        Assert.Equal("271", row.WindDirectionText);
        Assert.Equal("6.6", row.WindGustText);
        Assert.Equal("12", row.CloudCoverText);
        Assert.Equal("21.35", row.SkyQualityText);
        Assert.Equal("1.23", row.AirmassText);
        Assert.Equal("East", row.PierSideText);
        Assert.Equal("91.4", row.RotatorPositionText);
        Assert.Equal("-10.4", row.SensorTempText);
        Assert.Equal("100", row.CameraGainText);

        // The two identity fields the actions and Task 6 read.
        Assert.Equal(@"D:\Astro\M31\M31_Ha_001.fits", row.FilePath);
        Assert.Equal(frame.ImageId, row.ImageId);
    }

    [Fact]
    public void Rows_AbsentMetrics_RenderTheMissingDash()
    {
        // Null means "not measured", never zero (Task 1 handoff). A frame with nothing but a path
        // renders the missing dash (spec item 6, never a silent blank), and a null capture date
        // renders the dash in Time rather than an epoch.
        var row = Harness.Create(frames: [Frame()], display: EveryGroup()).Table.Rows[0];

        Assert.Equal(MetricText.Missing, row.TimeText);
        Assert.Equal(MetricText.Missing, row.FilterText);
        Assert.Equal(MetricText.Missing, row.ExposureText);
        Assert.Equal(MetricText.Missing, row.MedianHfrText);
        Assert.Equal(MetricText.Missing, row.DetectedStarsText);
        Assert.Equal(MetricText.Missing, row.AduMinText);
        Assert.Equal(MetricText.Missing, row.PierSideText);
        Assert.Equal(MetricText.Missing, row.CameraGainText);

        // A real zero is a measurement and shows as one.
        var zero = Harness.Create(frames: [Frame(cameraGain: 0)], display: EveryGroup()).Table.Rows[0];
        Assert.Equal("0", zero.CameraGainText);
    }

    [Fact]
    public void Rows_TimeColumn_HonoursTimezoneAndUse24hTime()
    {
        // Spec 5.8.1: general.timezone and general.use_24h_time are display formatting only. The
        // one formatter Task 4 owns does the work; this asserts the table reaches it with the
        // configured values and resolves the zone once for the whole table.
        IReadOnlyList<FrameRow> frames =
            [Frame(captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc))];

        var eastern = new GeneralSettings { Timezone = "Eastern Standard Time", Use24HTime = true };
        var twelveHour = new GeneralSettings { Timezone = "Eastern Standard Time", Use24HTime = false };

        var table = Harness.Create(frames: frames, display: EveryGroup(), general: eastern).Table;
        Assert.Equal("16:05", table.Rows[0].TimeText);

        // The header carries the zone's GMT offset (fixer-list item 20, re-pointed from the raw
        // stored id, which on Windows is a name like "Eastern Standard Time" rather than
        // something a reader parses at a glance), so a reader never has to guess which clock a
        // column of times is on. BaseUtcOffset is the standard offset, -05:00 for this zone,
        // never the -04:00 daylight offset.
        Assert.Equal("Time (GMT-05:00)", table.Columns[0].Title);

        Assert.Equal(
            "4:05 PM",
            Harness.Create(frames: frames, display: EveryGroup(), general: twelveHour).Table.Rows[0].TimeText);
    }

    [Fact]
    public void Rows_TimeColumn_CarriesTheLocalCaptureDate_OnlyWhenTheTableSpansNights()
    {
        // P25 R7: a table over a merged night prefixes each time with the frame's local capture
        // date as "MM-dd ", the web's TargetMetricsChart rule. The date is the zone's, not UTC's:
        // this instant is 09-04 in UTC and 09-03 in Eastern daylight time.
        IReadOnlyList<FrameRow> frames =
            [Frame(captureDate: new DateTime(2025, 9, 4, 0, 19, 0, DateTimeKind.Utc))];
        var twelveHour = new GeneralSettings { Timezone = "Eastern Standard Time", Use24HTime = false };

        Assert.Equal(
            "09-03 8:19 PM",
            Harness.Create(frames: frames, display: EveryGroup(), general: twelveHour, datePrefixed: true)
                .Table.Rows[0].TimeText);
        Assert.Equal(
            "8:19 PM",
            Harness.Create(frames: frames, display: EveryGroup(), general: twelveHour).Table.Rows[0].TimeText);
    }

    [Fact]
    public void Rows_GuidingRmsDagger_AppearsOnlyForPhd2_NeverForCsvOrNoSource()
    {
        // Spec 12.4 (Phase 15A): the dagger marks a figure measured somewhere other than beside
        // the frame. A csv figure is the frame's own sidecar and needs no disclosure (re-pointed
        // from the pre-Phase-15A rule, which drew the mark for any non-blank source; the collision
        // map's task7.md section 7.1 names this as the one existing case the phase moves).
        var phd2 = Harness.Create(
            frames: [Frame(guidingRmsArcsec: 0.45d, guidingRmsSource: "phd2")],
            display: EveryGroup()).Table.Rows[0];
        var csv = Harness.Create(
            frames: [Frame(guidingRmsArcsec: 0.45d, guidingRmsSource: "csv")],
            display: EveryGroup()).Table.Rows[0];
        var noSource = Harness.Create(
            frames: [Frame(guidingRmsArcsec: 0.45d)],
            display: EveryGroup()).Table.Rows[0];

        // 1. phd2: the dagger, and the tooltip and accessible name read the exact sentence.
        Assert.True(phd2.HasGuidingRmsSource);
        Assert.Equal("†", phd2.GuidingRmsSourceGlyph);
        Assert.Equal("from a PHD2 guide log", phd2.GuidingRmsSourceTooltip);

        // 2. csv: no glyph, and the mark is not visible.
        Assert.False(csv.HasGuidingRmsSource);
        Assert.Equal("", csv.GuidingRmsSourceGlyph);
        Assert.Null(csv.GuidingRmsSourceTooltip);

        // 3. null: no glyph, nothing drawn.
        Assert.False(noSource.HasGuidingRmsSource);
        Assert.Equal("", noSource.GuidingRmsSourceGlyph);
        Assert.Null(noSource.GuidingRmsSourceTooltip);
    }

    [Fact]
    public void Rows_FwhmColumn_ReadsFwhmNotMedianFwhm()
    {
        // Spec 7.1.1. median_fwhm is not on the read model at all, so the only way to get this
        // wrong is to read median_hfr into the FWHM cell, which is what this separates.
        var row = Harness.Create(
            frames: [Frame(medianHfr: 2.5d, fwhm: 1.75d)],
            display: EveryGroup()).Table.Rows[0];

        Assert.Equal("1.75", row.FwhmText);
        Assert.Equal("2.50", row.MedianHfrText);
    }

    // ---- actions --------------------------------------------------------------------------

    [Fact]
    public void Actions_Reveal_CallsShellIntegrationWithTheFramePath()
    {
        var harness = Harness.Create(frames: [Frame(filePath: @"D:\Astro\M31\one.fits")], display: EveryGroup());

        harness.Table.RevealFrameCommand.Execute(harness.Table.Rows[0]);

        var launch = Assert.Single(harness.Launches);
        Assert.Equal("explorer.exe", launch.FileName);
        Assert.Contains(@"D:\Astro\M31\one.fits", launch.Arguments);
        Assert.Empty(harness.Copies);
    }

    [Fact]
    public void Actions_OpenWith_CallsShellIntegrationWithTheFramePath()
    {
        var harness = Harness.Create(frames: [Frame(filePath: @"D:\Astro\M31\one.fits")], display: EveryGroup());

        harness.Table.OpenFrameWithDefaultApplicationCommand.Execute(harness.Table.Rows[0]);

        var launch = Assert.Single(harness.Launches);
        Assert.Equal(@"D:\Astro\M31\one.fits", launch.FileName);
        Assert.True(launch.UseShellExecute);
    }

    [Fact]
    public async Task Actions_CopyPath_CopiesTheAbsolutePath()
    {
        var harness = Harness.Create(frames: [Frame(filePath: @"D:\Astro\M31\one.fits")], display: EveryGroup());

        await harness.Table.CopyFramePathCommand.ExecuteAsync(harness.Table.Rows[0]);

        Assert.Equal([@"D:\Astro\M31\one.fits"], harness.Copies);
        Assert.Empty(harness.Launches);
    }

    [Fact]
    public void Actions_OpenPreview_IsDisabledWithoutADelegate()
    {
        // Phase 8 owns spec 11.5's modal. Until then the action renders disabled, which is what an
        // unbound seam should look like rather than a button that silently does nothing.
        var withoutDelegate = Harness.Create(frames: [Frame()], display: EveryGroup());
        Assert.False(withoutDelegate.Table.OpenPreviewCommand.CanExecute(withoutDelegate.Table.Rows[0]));

        withoutDelegate.Table.OpenPreviewCommand.Execute(withoutDelegate.Table.Rows[0]);
        Assert.Empty(withoutDelegate.Launches);

        var withDelegate = Harness.Create(frames: [Frame()], display: EveryGroup(), withPreview: true);
        Assert.True(withDelegate.Table.OpenPreviewCommand.CanExecute(withDelegate.Table.Rows[0]));

        withDelegate.Table.OpenPreviewCommand.Execute(withDelegate.Table.Rows[0]);
        Assert.Equal([withDelegate.Table.Rows[0]], withDelegate.Previews);
    }

    [Fact]
    public void OpenPreview_PassesTheCurrentRowsAndTheClickedIndex()
    {
        // Phase 8 Task 7: the modal navigates within the list it was opened from (spec 11.5), so
        // the seam hands over the rows and a position rather than a lone row.
        var harness = Harness.Create(
            frames: [Frame(fileName: "one.fits"), Frame(fileName: "two.fits"), Frame(fileName: "three.fits")],
            display: EveryGroup(),
            withPreview: true);

        harness.Table.OpenPreviewCommand.Execute(harness.Table.Rows[2]);

        var (rows, index) = Assert.Single(harness.PreviewCalls);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, index);
        Assert.Same(harness.Table.Rows[2], rows[index]);
    }

    [Fact]
    public void OpenPreview_PassesRowsInTheCurrentSortOrder()
    {
        // Not capture order: stepping with the arrow keys must follow what the user sees.
        var harness = Harness.Create(frames: Ascending(), display: EveryGroup(), withPreview: true);
        harness.Table.SortByCommand.Execute("file_name");
        harness.Table.SortByCommand.Execute("file_name");
        Assert.True(harness.Table.Descending);

        var clicked = harness.Table.Rows[1];
        harness.Table.OpenPreviewCommand.Execute(clicked);

        var (rows, index) = Assert.Single(harness.PreviewCalls);
        Assert.Equal([.. harness.Table.Rows], rows);
        Assert.Same(clicked, rows[index]);
    }

    [Fact]
    public void OpenPreview_NullDelegate_LeavesTheActionDisabled()
    {
        // The Phase 6 behaviour, unchanged by the widening: a null seam renders disabled rather
        // than as a button that silently does nothing.
        var harness = Harness.Create(frames: [Frame()], display: EveryGroup());

        Assert.False(harness.Table.OpenPreviewCommand.CanExecute(harness.Table.Rows[0]));
        harness.Table.OpenPreviewCommand.Execute(harness.Table.Rows[0]);

        Assert.Empty(harness.PreviewCalls);
    }

    [Fact]
    public void Actions_ShowRawHeaders_FlipsTheRowFlag()
    {
        // Task 6 puts the panel behind the flag; this task owns the flag and it is per row.
        var harness = Harness.Create(frames: [Frame(fileName: "one.fits"), Frame(fileName: "two.fits")], display: EveryGroup());
        // Counted per property rather than per event: Task 6 added RawHeaders to the row, which
        // the same toggle sets, so a bare event count is not this task's business.
        var changes = 0;
        harness.Table.Rows[0].PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FrameRowViewModel.AreRawHeadersExpanded)) changes++;
        };

        harness.Table.ToggleRawHeadersCommand.Execute(harness.Table.Rows[0]);

        Assert.True(harness.Table.Rows[0].AreRawHeadersExpanded);
        Assert.False(harness.Table.Rows[1].AreRawHeadersExpanded);
        Assert.Equal(1, changes);

        harness.Table.ToggleRawHeadersCommand.Execute(harness.Table.Rows[0]);
        Assert.False(harness.Table.Rows[0].AreRawHeadersExpanded);
    }

    [Fact]
    public async Task Actions_ShowRawHeaders_QueriesOncePerRowAcrossToggles()
    {
        // Review finding 7: the once-per-row contract lives at this seam, not just inside
        // RawHeaderPanelViewModel.Load's own idempotence -- ToggleRawHeaders must not build a
        // second panel (and so issue a second query) when a row is collapsed and re-expanded.
        //
        // F10, Phase 7 fixer item 3: the header stub runs inside RawHeaderPanelViewModel.Load's
        // Task.Run, so HeaderQueries is written off the calling thread. This test used to assert
        // the count without joining that task, and failed once in a full run under a concurrent
        // build for exactly that reason. Every assertion below now happens after the panel's
        // PendingLoad has completed. Null-guarded: a panel whose load never started (a row with
        // no image id) has no task to await.
        var harness = Harness.Create(frames: [Frame()], display: EveryGroup());
        var row = harness.Table.Rows[0];

        harness.Table.ToggleRawHeadersCommand.Execute(row); // expand: builds the panel, queries once
        var panel = row.RawHeaders;
        Assert.NotNull(panel);
        if (panel!.PendingLoad is { } firstLoad) await firstLoad;

        harness.Table.ToggleRawHeadersCommand.Execute(row); // collapse: flag only
        harness.Table.ToggleRawHeadersCommand.Execute(row); // re-expand: reuses the same panel
        if (row.RawHeaders?.PendingLoad is { } secondLoad) await secondLoad;

        Assert.Same(panel, row.RawHeaders);
        Assert.Equal([row.ImageId], harness.HeaderQueries);
    }

    [Fact]
    public async Task Actions_NullRow_IsANoOp()
    {
        // Every row command null-guards, the shape TargetListViewModel.OpenTarget already uses: a
        // command parameter can arrive null from a template whose DataContext has been cleared.
        var harness = Harness.Create(frames: [Frame()], display: EveryGroup());

        harness.Table.RevealFrameCommand.Execute(null);
        harness.Table.OpenFrameWithDefaultApplicationCommand.Execute(null);
        await harness.Table.CopyFramePathCommand.ExecuteAsync(null);
        harness.Table.ToggleRawHeadersCommand.Execute(null);
        harness.Table.ToggleColumnCommand.Execute(null);
        await harness.Pending;

        Assert.Empty(harness.Launches);
        Assert.Empty(harness.Copies);
        Assert.Equal(0, harness.Saves);
    }

    // ---- selection, the multi-row copy and reveal (P12 Task 6, R2) --------------------------

    /// <summary>Three frames whose paths differ, so an order assertion cannot pass by accident.
    /// </summary>
    private static IReadOnlyList<FrameRow> ThreePaths() =>
    [
        Frame(filePath: @"D:\Astro\M31\a.fits", fileName: "a.fits",
              captureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc), medianHfr: 3.0d),
        Frame(filePath: @"D:\Astro\M31\b.fits", fileName: "b.fits",
              captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc), medianHfr: 2.0d),
        Frame(filePath: @"D:\Astro\M31\c.fits", fileName: "c.fits",
              captureDate: new DateTime(2025, 12, 7, 21, 10, 0, DateTimeKind.Utc), medianHfr: 1.0d),
    ];

    private static void Select(FrameTableViewModel table, params int[] rowIndexes)
    {
        foreach (var index in rowIndexes)
        {
            table.SelectedRows.Add(table.Rows[index]);
        }
    }

    [Fact]
    public async Task CopySelectedPaths_ThreeRows_WritesThreeLinesWithATrailingNewline()
    {
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());
        Select(harness.Table, 0, 1, 2);

        await harness.Table.CopySelectedPathsCommand.ExecuteAsync(null);

        // The separator is asserted rather than assumed: it is whatever the target-level copy
        // uses, and the two must not be able to disagree (P12 R2).
        var expected = string.Join(
            Environment.NewLine,
            new[] { @"D:\Astro\M31\a.fits", @"D:\Astro\M31\b.fits", @"D:\Astro\M31\c.fits" })
            + Environment.NewLine;

        Assert.Equal([expected], harness.Copies);
        Assert.Equal(3, expected.Split(Environment.NewLine).Length - 1);
    }

    [Fact]
    public async Task CopySelectedPaths_PreservesTheTableOrder()
    {
        // Selected bottom-up and with the table sorted, so neither click order nor capture order
        // can produce the expected string: the copy reads Rows, which is what the user sees.
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());
        harness.Table.SortByCommand.Execute("median_hfr");
        Assert.Equal(["c.fits", "b.fits", "a.fits"], harness.Table.Rows.Select(row => row.FileName));

        Select(harness.Table, 2, 0);

        await harness.Table.CopySelectedPathsCommand.ExecuteAsync(null);

        Assert.Equal(
            [@"D:\Astro\M31\c.fits" + Environment.NewLine + @"D:\Astro\M31\a.fits" + Environment.NewLine],
            harness.Copies);
    }

    [Fact]
    public async Task CopySelectedPaths_MatchesTheTargetLevelFrameListFormat()
    {
        // P12 R2: the selection copy writes the target-level "Copy frame list" format, byte for
        // byte. The target-level command is one line, _shell.CopyFrameListAsync(FramePaths), so
        // calling that member on the same paths is the same comparison without standing a whole
        // TargetDetailViewModel up.
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());
        Select(harness.Table, 0, 1, 2);

        await harness.Table.CopySelectedPathsCommand.ExecuteAsync(null);
        await harness.Shell.CopyFrameListAsync(
            [.. harness.Table.Rows.Select(row => row.PathForCopy)]);

        Assert.Equal(2, harness.Copies.Count);
        Assert.Equal(harness.Copies[0], harness.Copies[1]);
    }

    [Fact]
    public void CopySelectedPaths_IsDisabled_WithNoSelection()
    {
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());

        Assert.False(harness.Table.CopySelectedPathsCommand.CanExecute(null));
        Assert.Equal("Copy paths (0)", harness.Table.CopySelectedPathsText);

        Select(harness.Table, 1);

        Assert.True(harness.Table.CopySelectedPathsCommand.CanExecute(null));
        Assert.Equal("Copy paths (1)", harness.Table.CopySelectedPathsText);
    }

    [Fact]
    public async Task CopySelectedPaths_WithNoSelection_CopiesNothing()
    {
        // RelayCommand.Execute ignores CanExecute, so the empty case is reachable. It writes no
        // clipboard at all rather than an empty one, because it shares the target-level helper.
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());

        await harness.Table.CopySelectedPathsCommand.ExecuteAsync(null);

        Assert.Empty(harness.Copies);
    }

    [Fact]
    public void RevealSelected_IsEnabled_AtExactlyOneRow()
    {
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());
        Select(harness.Table, 1);

        Assert.True(harness.Table.RevealSelectedCommand.CanExecute(null));

        harness.Table.RevealSelectedCommand.Execute(null);

        var launch = Assert.Single(harness.Launches);
        Assert.Contains(@"D:\Astro\M31\b.fits", launch.Arguments);
    }

    [Fact]
    public void RevealSelected_IsDisabled_AtZeroAndAtTwo()
    {
        // explorer.exe /select highlights one file; a two-row selection has no single answer.
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());

        Assert.False(harness.Table.RevealSelectedCommand.CanExecute(null));

        Select(harness.Table, 0);
        Assert.True(harness.Table.RevealSelectedCommand.CanExecute(null));

        Select(harness.Table, 1);
        Assert.False(harness.Table.RevealSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void RevealSelected_AtTwoRows_RevealsNothing()
    {
        // RelayCommand.Execute ignores CanExecute (TRACKING section 6 item 13), which is why the
        // command body repeats the count check.
        var harness = Harness.Create(frames: ThreePaths(), display: EveryGroup());
        Select(harness.Table, 0, 1);

        harness.Table.RevealSelectedCommand.Execute(null);

        Assert.Empty(harness.Launches);
    }

    // ---- the outlier filter (P12 R3, ruling Q14) --------------------------------------------

    /// <summary>Five frames: two flagged for HFR, one for eccentricity, two clean.</summary>
    private static IReadOnlyList<FrameRow> FlaggedFrames() =>
    [
        Frame(fileName: "clean_1.fits", captureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc),
              medianHfr: 2.0d, eccentricity: 0.30d),
        Frame(fileName: "hfr_low.fits", captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc),
              medianHfr: 2.8d, eccentricity: 0.31d, isHfrOutlier: true),
        Frame(fileName: "ecc.fits", captureDate: new DateTime(2025, 12, 7, 21, 10, 0, DateTimeKind.Utc),
              medianHfr: 2.1d, eccentricity: 0.72d, isEccentricityOutlier: true),
        Frame(fileName: "hfr_high.fits", captureDate: new DateTime(2025, 12, 7, 21, 15, 0, DateTimeKind.Utc),
              medianHfr: 3.4d, eccentricity: 0.33d, isHfrOutlier: true),
        Frame(fileName: "clean_2.fits", captureDate: new DateTime(2025, 12, 7, 21, 20, 0, DateTimeKind.Utc),
              medianHfr: 2.2d, eccentricity: 0.29d),
    ];

    [Fact]
    public void SetOutlierFilter_Hfr_ShowsExactlyTheFlaggedRows()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        Assert.Equal(FrameOutlierFilter.Hfr, harness.Table.OutlierFilter);
        Assert.True(harness.Table.IsFiltered);
        Assert.All(harness.Table.Rows, row => Assert.True(row.IsHfrOutlier));
        Assert.Equal(FlaggedFrames().Count(frame => frame.IsHfrOutlier), harness.Table.Rows.Count);
    }

    [Fact]
    public void SetOutlierFilter_Hfr_SortsHfrDescending()
    {
        // panel-workflow.md T3: the first header click is ascending, so a user answering "which
        // are the worst" would have had to click twice. The action does it for them.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        Assert.Equal("median_hfr", harness.Table.SortKey);
        Assert.True(harness.Table.Descending);
        Assert.Equal(["hfr_high.fits", "hfr_low.fits"], harness.Table.Rows.Select(row => row.FileName));
    }

    [Theory]
    [InlineData(FrameOutlierFilter.Fwhm, "fwhm.fits")]
    [InlineData(FrameOutlierFilter.Stars, "stars.fits")]
    [InlineData(FrameOutlierFilter.GuidingRms, "rms.fits")]
    public void SetOutlierFilter_AP24Metric_ShowsExactlyItsOwnFlaggedRow(FrameOutlierFilter filter, string expected)
    {
        // P24 R22: red if a metric's filter reads another metric's flag, or shows a clean row.
        IReadOnlyList<FrameRow> frames =
        [
            .. FlaggedFrames(),
            Frame(fileName: "fwhm.fits", captureDate: new DateTime(2025, 12, 7, 21, 25, 0, DateTimeKind.Utc), isFwhmOutlier: true),
            Frame(fileName: "stars.fits", captureDate: new DateTime(2025, 12, 7, 21, 30, 0, DateTimeKind.Utc), isStarsOutlier: true),
            Frame(fileName: "rms.fits", captureDate: new DateTime(2025, 12, 7, 21, 35, 0, DateTimeKind.Utc), isGuidingRmsOutlier: true),
        ];
        var harness = Harness.Create(frames: frames, display: EveryGroup());

        harness.Table.SetOutlierFilter(filter);

        Assert.Equal([expected], harness.Table.Rows.Select(row => row.FileName));
        Assert.Equal("", harness.Table.SortKey);
    }

    [Fact]
    public void SetOutlierFilter_Eccentricity_ShowsExactlyTheFlaggedRows()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Eccentricity);

        Assert.Equal(["ecc.fits"], harness.Table.Rows.Select(row => row.FileName));
        Assert.All(harness.Table.Rows, row => Assert.True(row.IsEccentricityOutlier));

        // The eccentricity action does not re-sort: only the HFR one answers "which are the
        // worst" against a column the table orders by on its own.
        Assert.Equal("", harness.Table.SortKey);
    }

    [Fact]
    public void SetOutlierFilter_None_RestoresEveryRow()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        harness.Table.ClearOutlierFilterCommand.Execute(null);

        Assert.Equal(FrameOutlierFilter.None, harness.Table.OutlierFilter);
        Assert.False(harness.Table.IsFiltered);
        Assert.Equal(5, harness.Table.Rows.Count);
    }

    [Fact]
    public void SetOutlierFilter_DropsASelectedRowThatIsFilteredOut()
    {
        // A row the user cannot see must not contribute a path to a copy.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        Select(harness.Table, 0, 1);
        Assert.Equal(2, harness.Table.SelectedRows.Count);

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        var kept = Assert.Single(harness.Table.SelectedRows);
        Assert.Equal("hfr_low.fits", kept.FileName);
    }

    [Fact]
    public void SetOutlierFilter_KeepsTheSort_WhenCleared()
    {
        // Ruling Q14: clearing keeps the sort. Re-sorting on clear would move rows under a
        // pointer about to click one.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        harness.Table.SetOutlierFilter(FrameOutlierFilter.None);

        Assert.Equal("median_hfr", harness.Table.SortKey);
        Assert.True(harness.Table.Descending);
        Assert.Equal(5, harness.Table.Rows.Count);
        Assert.Equal("hfr_high.fits", harness.Table.Rows[0].FileName);
        Assert.Equal("clean_1.fits", harness.Table.Rows[^1].FileName);
    }

    [Fact]
    public void ClearFilterOrSelection_ClearsTheFilterBeforeTheSelection()
    {
        // Ruling Q14: Escape clears the filter before the selection, so one press never does
        // both and a user who filtered and then selected steps back out the way they came.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Select(harness.Table, 0);

        harness.Table.ClearFilterOrSelectionCommand.Execute(null);

        Assert.Equal(FrameOutlierFilter.None, harness.Table.OutlierFilter);
        Assert.Single(harness.Table.SelectedRows);

        harness.Table.ClearFilterOrSelectionCommand.Execute(null);

        Assert.Empty(harness.Table.SelectedRows);
    }

    [Fact]
    public void ClearFilterOrSelection_IsDisabled_WithNoFilterAndNoSelection()
    {
        // Review P2-1 and ruling (b). The enablement decides whether the table's Escape
        // KeyBinding marks the key event handled: with neither a filter nor a selection to clear
        // the press has to bubble out to the page's drawer-then-back handler. The body repeats
        // the guard because RelayCommand.Execute ignores CanExecute.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        Assert.False(harness.Table.ClearFilterOrSelectionCommand.CanExecute(null));

        harness.Table.ClearFilterOrSelectionCommand.Execute(null);

        Assert.Equal(FrameOutlierFilter.None, harness.Table.OutlierFilter);
        Assert.Empty(harness.Table.SelectedRows);
        Assert.Equal(5, harness.Table.Rows.Count);

        // A selection alone enables it, and clearing that selection disables it again.
        Select(harness.Table, 0);
        Assert.True(harness.Table.ClearFilterOrSelectionCommand.CanExecute(null));

        harness.Table.ClearFilterOrSelectionCommand.Execute(null);
        Assert.False(harness.Table.ClearFilterOrSelectionCommand.CanExecute(null));

        // A filter alone enables it too, and clearing that filter disables it again.
        harness.Table.SetOutlierFilter(FrameOutlierFilter.Eccentricity);
        Assert.True(harness.Table.ClearFilterOrSelectionCommand.CanExecute(null));

        harness.Table.ClearFilterOrSelectionCommand.Execute(null);
        Assert.False(harness.Table.ClearFilterOrSelectionCommand.CanExecute(null));
    }

    // ---- the count line and the copy label ---------------------------------------------------

    [Fact]
    public void ShownCountText_Unfiltered_NamesTheTotal()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        Assert.Equal("5 frames", harness.Table.ShownCountText);
    }

    [Fact]
    public void ShownCountText_Filtered_NamesBothCounts()
    {
        // The threshold is not reproduced here: it lives in the insight's prose, which
        // SessionDetailQuery's own rule forbids parsing, and the findings line above the table
        // already prints it.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Assert.Equal("2 of 5 shown, HFR outliers", harness.Table.ShownCountText);

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Eccentricity);
        Assert.Equal("1 of 5 shown, eccentricity outliers", harness.Table.ShownCountText);
    }

    [Fact]
    public void ShownCountText_WithASelection_NamesTheSelectionCount()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        // One clean row and one flagged row, so the filter drops exactly one of the two and the
        // clause follows the selection rather than restating it.
        Select(harness.Table, 0, 1);

        Assert.Equal("5 frames, 2 selected", harness.Table.ShownCountText);

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        Assert.Equal("2 of 5 shown, HFR outliers, 1 selected", harness.Table.ShownCountText);
    }

    [Fact]
    public void ShownCountText_OneFrame_ReadsSingular()
    {
        var harness = Harness.Create(frames: [Frame()], display: EveryGroup());

        Assert.Equal("1 frame", harness.Table.ShownCountText);
    }

    // ---- the night strip's hand-over (Task 5 consumes this) ----------------------------------

    [Fact]
    public void SelectFrameAt_SelectsThatCaptureIndexOnly()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        Select(harness.Table, 0);

        harness.Table.SelectFrameAt(3);

        var row = Assert.Single(harness.Table.SelectedRows);
        Assert.Equal("hfr_high.fits", row.FileName);
    }

    [Fact]
    public void SelectFrameAt_AFilteredOutIndex_ChangesNothing()
    {
        // The strip can name a frame the table is not showing.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Select(harness.Table, 0);
        var before = harness.Table.SelectedRows[0];

        harness.Table.SelectFrameAt(0);

        Assert.Same(before, Assert.Single(harness.Table.SelectedRows));
    }

    [Fact]
    public void SelectFrameAt_AnOutOfRangeIndex_DoesNotThrow()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.SelectFrameAt(-1);
        harness.Table.SelectFrameAt(5);
        harness.Table.SelectFrameAt(int.MaxValue);

        Assert.Empty(harness.Table.SelectedRows);
    }

    // ---- the header's own columns -------------------------------------------------------------

    [Fact]
    public void Columns_CarryFrameColumnsIsNumeric()
    {
        // The join spec 14.4 needs: the header cell aligns over the figures rather than at the
        // opposite edge, and it reads the same flag the row cells read.
        var harness = Harness.Create(display: EveryGroup());

        Assert.Equal(
            FrameColumns.All.Select(column => column.IsNumeric),
            harness.Table.Columns.Select(column => column.IsNumeric));
    }

    [Fact]
    public void Columns_HeadersUseTheSharedUnitsAndSentenceCase()
    {
        // Spec items 4 and 5: the unit in the header in the inch-mark style, the six shared
        // headings from TableHeads, sentence case, and a tooltip on every header. FrameColumns.All
        // is Core and keeps spec 12.4's verbatim titles; the rewrite is a display concern.
        var harness = Harness.Create(display: EveryGroup());

        ColumnViewModel Column(string key) => harness.Table.Columns.Single(column => column.Key == key);
        string Title(string key) => Column(key).Title;

        Assert.Equal(TableHeads.Hfr, Title("median_hfr"));
        Assert.Equal(TableHeads.Ecc, Title("eccentricity"));
        Assert.Equal(TableHeads.Fwhm, Title("fwhm"));
        Assert.Equal(TableHeads.Rms, Title("guiding_rms_arcsec"));
        Assert.Equal(TableHeads.Stars, Title("detected_stars"));
        Assert.Equal(TableHeads.Exposure, Title("exposure_time"));
        Assert.Equal("RMS RA \"", Title("guiding_rms_ra_arcsec"));
        Assert.Equal("RMS Dec \"", Title("guiding_rms_dec_arcsec"));

        Assert.Equal("ADU mean", Title("adu_mean"));
        Assert.Equal("ADU median", Title("adu_median"));
        Assert.Equal("ADU min", Title("adu_min"));
        Assert.Equal("ADU max", Title("adu_max"));
        Assert.Equal("Focus temp", Title("focuser_temp"));
        Assert.Equal("Ambient temp", Title("ambient_temp"));
        Assert.Equal("Dew point", Title("dew_point"));
        Assert.Equal("Wind dir", Title("wind_direction"));
        Assert.Equal("Temp C", Title("sensor_temp"));

        // The Time column keeps its zone's GMT offset (fixer-list item 20).
        Assert.Equal("Time (GMT+00:00)", Title("time"));

        Assert.All(harness.Table.Columns, column => Assert.DoesNotContain("arcsec", column.Title));

        // Every header has a tip: an abbreviation says what it stands for, and any other title
        // carries itself, so a text title dragged narrow enough to trim still reads in full.
        Assert.All(harness.Table.Columns, column => Assert.NotNull(column.Tip));
        Assert.Equal(TableHeads.HfrTip, Column("median_hfr").Tip);
        Assert.Equal(TableHeads.EccTip, Column("eccentricity").Tip);
        Assert.Equal(TableHeads.FwhmTip, Column("fwhm").Tip);
        Assert.Equal(TableHeads.RmsTip, Column("guiding_rms_arcsec").Tip);
        Assert.Equal(TableHeads.ExposureTip, Column("exposure_time").Tip);
        Assert.Equal("File name", Column("file_name").Tip);
        Assert.Equal("Time (GMT+00:00)", Column("time").Tip);
    }

    [Fact]
    public void Rows_ExposureCell_CarriesNoUnitSuffix()
    {
        // The unit moved to the header, so the cell is a bare figure like every other numeric
        // cell. ExposureText was the only cell that carried a suffix.
        var harness = Harness.Create(frames: [Frame(exposureTime: 300d)], display: EveryGroup());

        Assert.Equal("300", harness.Table.Rows[0].ExposureText);
    }

    [Fact]
    public void Rows_CarryTheTwoOutlierFlags()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        Assert.Equal([false, true, false, true, false], harness.Table.Rows.Select(row => row.IsHfrOutlier));
        Assert.Equal([false, false, true, false, false], harness.Table.Rows.Select(row => row.IsEccentricityOutlier));
        Assert.Equal([false, true, true, true, false], harness.Table.Rows.Select(row => row.IsOutlier));
        Assert.Equal(
            harness.Table.Rows.Select(row => row.FilePath),
            harness.Table.Rows.Select(row => row.PathForCopy));
    }

    // ---- Phase 13 Task 6: R6's measured column, R9's highlight, R8 and R9's select-and-preview --

    [Fact]
    public void CellTextsToMeasure_IsTheWholeNight_NotTheFilteredSubset()
    {
        // Ruling Q9. The view measures this list, and a filter must not resize a column: measuring
        // the filtered subset would narrow the table while a filter is on and widen it again on
        // clear, moving every column to its right under a pointer about to click one.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        Assert.Equal(
            ["clean_1.fits", "hfr_low.fits", "ecc.fits", "hfr_high.fits", "clean_2.fits"],
            harness.Table.CellTextsToMeasure("file_name"));
        Assert.Equal(["2.00", "2.80", "2.10", "3.40", "2.20"], harness.Table.CellTextsToMeasure("median_hfr"));
        Assert.Empty(harness.Table.CellTextsToMeasure("no_such_column"));

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        Assert.Equal(2, harness.Table.Rows.Count);
        Assert.Equal(5, harness.Table.CellTextsToMeasure("file_name").Count);
    }

    [Fact]
    public void ColumnWidths_StartAtTheFloor_UntilTheViewMeasures()
    {
        // R5: the table renders before the view has measured anything, so every column is seeded
        // at the floor rather than at zero or NaN, and nothing is stored on a fresh profile.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        Assert.Equal(48d, FrameTableViewModel.ColumnFloor);
        Assert.All(harness.Table.Columns, column => Assert.Equal(FrameTableViewModel.ColumnFloor, column.Width));
        Assert.All(harness.Table.Columns, column => Assert.False(harness.Table.HasStoredWidth(column.Key)));
    }

    [Fact]
    public void SetAutoFitWidth_AppliesToAColumnWithNoStoredWidth_AndIsFloored()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        var fileName = harness.Table.Columns.Single(column => column.Key == "file_name");

        harness.Table.SetAutoFitWidth("file_name", 312.5d);
        Assert.Equal(312.5d, fileName.Width);

        harness.Table.SetAutoFitWidth("file_name", 12d);
        Assert.Equal(FrameTableViewModel.ColumnFloor, fileName.Width);

        // A measurement taken before the control has a typeface can produce these, and a NaN
        // width reaches layout as a silently unmeasurable column.
        harness.Table.SetAutoFitWidth("file_name", 300d);
        harness.Table.SetAutoFitWidth("file_name", double.NaN);
        harness.Table.SetAutoFitWidth("file_name", double.PositiveInfinity);
        harness.Table.SetAutoFitWidth("file_name", 0d);
        Assert.Equal(300d, fileName.Width);
        Assert.Equal(0, harness.Saves);
    }

    [Fact]
    public void SetColumnWidth_HoldsTheFloorUnderADrag_AndStoresNothing()
    {
        // The floor holds under a drag: a column can be narrowed to it and no further, so it can
        // never be dragged out of existence. The live width writes nothing until the release.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        var hfr = harness.Table.Columns.Single(column => column.Key == "median_hfr");

        harness.Table.SetColumnWidth("median_hfr", 140d);
        Assert.Equal(140d, hfr.Width);

        harness.Table.SetColumnWidth("median_hfr", 3d);
        Assert.Equal(FrameTableViewModel.ColumnFloor, hfr.Width);

        harness.Table.SetColumnWidth("median_hfr", double.NaN);
        Assert.Equal(FrameTableViewModel.ColumnFloor, hfr.Width);
        Assert.False(harness.Table.HasStoredWidth("median_hfr"));
        Assert.Equal(0, harness.Saves);
    }

    [Fact]
    public void SetColumnWidth_ANumericColumn_StopsAtItsAutoFit()
    {
        // Spec item 3: a number never trims, so a drag stops a numeric column at its measured fit.
        // A text column may trim and still narrows to the bare floor.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        var hfr = harness.Table.Columns.Single(column => column.Key == "median_hfr");
        var fileName = harness.Table.Columns.Single(column => column.Key == "file_name");

        harness.Table.SetAutoFitWidth("median_hfr", 70d);
        harness.Table.SetColumnWidth("median_hfr", 50d);
        Assert.Equal(70d, hfr.Width);

        harness.Table.SetAutoFitWidth("file_name", 200d);
        harness.Table.SetColumnWidth("file_name", 50d);
        Assert.Equal(50d, fileName.Width);
    }

    [Fact]
    public async Task AStoredNumericWidth_UnderItsAutoFit_RendersAtTheAutoFit_AndStaysStored()
    {
        // A stored width narrower than a numeric column's fit (a larger font since the drag) draws
        // at the fit, and the stored figure is never rewritten by the font change.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.SetColumnWidth("fwhm", 50d);
        harness.Table.StoreColumnWidth("fwhm");
        await harness.Pending;
        var saves = harness.Saves;

        var table = harness.NewTable(harness.Display, FlaggedFrames());
        var fwhm = table.Columns.Single(column => column.Key == "fwhm");
        table.SetAutoFitWidth("fwhm", 70d);

        Assert.Equal(70d, fwhm.Width);
        Assert.True(table.HasStoredWidth("fwhm"));
        Assert.Equal(50d, harness.Display.ColumnWidthsFor(DisplaySettings.FramesTableId)["fwhm"]);
        Assert.Equal(saves, harness.Saves);
    }

    [Fact]
    public async Task StoreColumnWidth_WritesTheWidth_AndATableBuiltAfterwardsStartsFromIt()
    {
        // A drag stores and survives a reload: the width lands in display.column_widths.frames
        // under the column key, and the next night, built from the saved document or from the
        // startup snapshot plus the writer memo, seeds that width and keeps it over the auto-fit
        // the view then measures.
        var snapshot = EveryGroup();
        var harness = Harness.Create(frames: FlaggedFrames(), display: snapshot);

        harness.Table.SetColumnWidth("file_name", 333d);
        harness.Table.StoreColumnWidth("file_name");
        await harness.Pending;

        Assert.True(harness.Table.HasStoredWidth("file_name"));
        Assert.Equal(1, harness.Saves);
        Assert.Equal(333d, harness.Display.ColumnWidthsFor(DisplaySettings.FramesTableId)["file_name"]);
        Assert.Equal(
            EveryGroup().ColumnsFor(DisplaySettings.FramesTableId),
            harness.Display.ColumnsFor(DisplaySettings.FramesTableId));

        var reloaded = harness.NewTable(harness.Display, FlaggedFrames());
        var fromSnapshot = harness.NewTable(snapshot, FlaggedFrames());
        foreach (var table in new[] { reloaded, fromSnapshot })
        {
            var fileName = table.Columns.Single(column => column.Key == "file_name");
            Assert.Equal(333d, fileName.Width);
            Assert.True(table.HasStoredWidth("file_name"));

            table.SetAutoFitWidth("file_name", 500d);
            Assert.Equal(333d, fileName.Width);
        }
    }

    [Fact]
    public async Task ResetColumnWidth_ClearsTheStoredWidth_AndReturnsTheColumnToItsAutoFit()
    {
        // The double-click on the divider: the stored entry goes, the document loses the key, the
        // column takes the auto-fit the view last measured, and a table built afterwards no
        // longer sees a stored width.
        var snapshot = EveryGroup();
        var harness = Harness.Create(frames: FlaggedFrames(), display: snapshot);
        var fileName = harness.Table.Columns.Single(column => column.Key == "file_name");
        harness.Table.SetAutoFitWidth("file_name", 280d);
        harness.Table.SetColumnWidth("file_name", 400d);
        harness.Table.StoreColumnWidth("file_name");

        harness.Table.ResetColumnWidth("file_name");
        await harness.Pending;

        Assert.Equal(280d, fileName.Width);
        Assert.False(harness.Table.HasStoredWidth("file_name"));
        Assert.Equal(2, harness.Saves);
        Assert.Empty(harness.Display.ColumnWidthsFor(DisplaySettings.FramesTableId));
        Assert.False(harness.Display.ColumnWidths.ContainsKey(DisplaySettings.FramesTableId));

        var next = harness.NewTable(snapshot, FlaggedFrames());
        Assert.False(next.HasStoredWidth("file_name"));
        Assert.Equal(FrameTableViewModel.ColumnFloor, next.Columns.Single(column => column.Key == "file_name").Width);

        // A reset of a column with nothing stored writes nothing and falls back to the floor
        // when nothing has been measured either.
        harness.Table.ResetColumnWidth("fwhm");
        await harness.Pending;
        Assert.Equal(2, harness.Saves);
        Assert.Equal(FrameTableViewModel.ColumnFloor, harness.Table.Columns.Single(column => column.Key == "fwhm").Width);
    }

    [Fact]
    public async Task AHiddenColumn_KeepsItsStoredWidth()
    {
        // Hiding a column through the picker and showing it again must not clear what the user
        // dragged: the width belongs to the column, not to the visible list.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        var fwhm = harness.Table.Columns.Single(column => column.Key == "fwhm");
        harness.Table.SetColumnWidth("fwhm", 123d);
        harness.Table.StoreColumnWidth("fwhm");

        harness.Table.ToggleColumnCommand.Execute(fwhm);
        await harness.Pending;
        Assert.False(fwhm.IsShown);
        Assert.Equal(123d, fwhm.Width);
        Assert.True(harness.Table.HasStoredWidth("fwhm"));

        harness.Table.SetAutoFitWidth("fwhm", 60d);
        harness.Table.ToggleColumnCommand.Execute(fwhm);
        await harness.Pending;
        Assert.True(fwhm.IsShown);
        Assert.Equal(123d, fwhm.Width);
        Assert.Equal(123d, harness.Display.ColumnWidthsFor(DisplaySettings.FramesTableId)["fwhm"]);
    }

    [Fact]
    public void HighlightFrameAt_SetsTheHighlightedRow_AndItsFlag()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.HighlightFrameAt(3);

        Assert.Equal("hfr_high.fits", harness.Table.HighlightedRow!.FileName);
        Assert.True(harness.Table.HighlightedRow.IsHighlighted);
    }

    [Fact]
    public void HighlightFrameAt_ClearsThePreviousRowsFlag()
    {
        // The pointer crosses ticks one after another, so exactly one row may carry the tint.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());

        harness.Table.HighlightFrameAt(1);
        var first = harness.Table.HighlightedRow!;

        harness.Table.HighlightFrameAt(4);

        Assert.False(first.IsHighlighted);
        Assert.True(harness.Table.HighlightedRow!.IsHighlighted);
        Assert.Single(harness.Table.Rows, row => row.IsHighlighted);
    }

    [Fact]
    public void HighlightFrameAt_Null_ClearsTheHighlight()
    {
        // R9: leaving the strip clears the tint.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.HighlightFrameAt(2);
        var row = harness.Table.HighlightedRow!;

        harness.Table.HighlightFrameAt(null);

        Assert.Null(harness.Table.HighlightedRow);
        Assert.False(row.IsHighlighted);
    }

    [Fact]
    public void HighlightFrameAt_AnOutOfRangeIndex_ClearsIt_AndDoesNotThrow()
    {
        // Ruling Q13: a miss clears rather than leaving a stale tint on a row the pointer is no
        // longer over. This is where it differs from SelectFrameAt, which returns early.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.HighlightFrameAt(0);

        harness.Table.HighlightFrameAt(-1);
        Assert.Null(harness.Table.HighlightedRow);

        harness.Table.HighlightFrameAt(0);
        harness.Table.HighlightFrameAt(5);
        Assert.Null(harness.Table.HighlightedRow);

        harness.Table.HighlightFrameAt(int.MaxValue);
        Assert.Null(harness.Table.HighlightedRow);
        Assert.DoesNotContain(harness.Table.Rows, row => row.IsHighlighted);
    }

    [Fact]
    public void HighlightFrameAt_AFilteredOutIndex_ClearsTheHighlight()
    {
        // R9's "a hover on a frame the current outlier filter hides highlights nothing", in the
        // case that matters: a tint is already on a shown row when the pointer reaches a tick the
        // filter has taken off the table.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        harness.Table.HighlightFrameAt(3);
        var shown = harness.Table.HighlightedRow!;
        Assert.Equal("hfr_high.fits", shown.FileName);

        harness.Table.HighlightFrameAt(0);

        Assert.Null(harness.Table.HighlightedRow);
        Assert.False(shown.IsHighlighted);
    }

    [Fact]
    public void Highlighting_ChangesNoSelection()
    {
        // A hover must not change what Copy paths would copy.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        Select(harness.Table, 1);

        harness.Table.HighlightFrameAt(4);

        var selected = Assert.Single(harness.Table.SelectedRows);
        Assert.Equal("hfr_low.fits", selected.FileName);
        Assert.Equal("clean_2.fits", harness.Table.HighlightedRow!.FileName);
        Assert.False(selected.IsHighlighted);
    }

    [Fact]
    public void Project_ClearsAHighlightTheFilterRemoved()
    {
        // The filter can remove the hovered row while the pointer is still over its tick.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup());
        harness.Table.HighlightFrameAt(0);
        var row = harness.Table.HighlightedRow!;

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        Assert.Null(harness.Table.HighlightedRow);
        Assert.False(row.IsHighlighted);

        // A sort, which re-projects with every row a survivor, keeps one.
        harness.Table.HighlightFrameAt(3);
        harness.Table.SortByCommand.Execute("median_hfr");

        Assert.Equal("hfr_high.fits", harness.Table.HighlightedRow!.FileName);
    }

    [Fact]
    public void SelectAndPreviewFrameAt_SelectsTheRow_AndOpensThePreviewAtIt()
    {
        // R8 and R9 are one action: a plain row click and a tick click both come through here.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup(), withPreview: true);

        harness.Table.SelectAndPreviewFrameAt(3);

        var selected = Assert.Single(harness.Table.SelectedRows);
        Assert.Equal("hfr_high.fits", selected.FileName);

        var opened = Assert.Single(harness.PreviewCalls);
        Assert.Same(selected, opened.Rows[opened.Index]);
    }

    [Fact]
    public void SelectAndPreviewFrameAt_AFilteredOutIndex_SelectsNothing_AndOpensNothing()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup(), withPreview: true);
        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        harness.Table.SelectAndPreviewFrameAt(0);

        Assert.Empty(harness.Table.SelectedRows);
        Assert.Empty(harness.PreviewCalls);
    }

    [Fact]
    public void SelectAndPreviewFrameAt_AFilteredOutIndexWithALiveSelection_OpensNothing()
    {
        // The selection is read back to decide whether to open, so a filtered-out index reached
        // while another row is already selected must not open the preview at that other row.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup(), withPreview: true);
        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Select(harness.Table, 0);
        var before = harness.Table.SelectedRows[0];

        harness.Table.SelectAndPreviewFrameAt(0);

        Assert.Same(before, Assert.Single(harness.Table.SelectedRows));
        Assert.Empty(harness.PreviewCalls);
    }

    [Fact]
    public void SelectAndPreviewFrameAt_AnOutOfRangeIndex_OpensNothing()
    {
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup(), withPreview: true);

        harness.Table.SelectAndPreviewFrameAt(-1);
        harness.Table.SelectAndPreviewFrameAt(5);
        harness.Table.SelectAndPreviewFrameAt(int.MaxValue);

        Assert.Empty(harness.Table.SelectedRows);
        Assert.Empty(harness.PreviewCalls);
    }

    [Fact]
    public void SelectAndPreviewFrameAt_OpensOnTheSortedList_AtTheRowsIndex()
    {
        // Spec 11.5's list is the one the user is looking at, so the index handed over is the
        // row's place in the sorted table rather than its place in capture order.
        var harness = Harness.Create(frames: FlaggedFrames(), display: EveryGroup(), withPreview: true);
        harness.Table.SortByCommand.Execute("median_hfr");

        harness.Table.SelectAndPreviewFrameAt(4);

        var opened = Assert.Single(harness.PreviewCalls);
        Assert.Equal(harness.Table.Rows, opened.Rows);
        Assert.Equal(harness.Table.Rows.IndexOf(harness.Table.SelectedRows[0]), opened.Index);
        Assert.Equal("clean_2.fits", opened.Rows[opened.Index].FileName);
        Assert.NotEqual(4, opened.Index);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // Task 5 implementer escalation 3: _lifetime.Cancel() ran after _lifetime.Dispose(), so a
        // second call threw ObjectDisposedException from inside a disposal. No production path
        // disposes twice today, which is why nothing caught it; IDisposable requires it anyway,
        // and the card disposes this table through a cast that a future second owner could repeat.
        var harness = Harness.Create(frames: FlaggedFrames());

        harness.Table.Dispose();
        harness.Table.Dispose();
        harness.Table.Dispose();
    }
}
