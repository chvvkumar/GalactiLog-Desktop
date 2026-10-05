using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests builds a Statistics page and the stats response behind it, so a later
/// constructor or read-model change is one edit rather than seven. No database, no window and no
/// dispatcher: every delegate is a lambda and the post seam runs its closure inline
/// (design-spec 18.3).
/// </summary>
internal static class StatisticsViewModelTestFactory
{
    /// <summary>The clock every test pins, so the calendar's rolling range and the timeline's
    /// efficiency horizon are fixed.</summary>
    public static readonly DateOnly Today = new(2025, 6, 15);

    /// <summary>Flagstaff, the spec 18.1 row Task 1's tests already use.</summary>
    public const double Latitude = 35.198d;

    public const double Longitude = -111.651d;

    public static GeneralSettings WithCoordinates() => new()
    {
        ObserverLatitude = Latitude,
        ObserverLongitude = Longitude,
    };

    public static GeneralSettings WithoutCoordinates() => new();

    /// <summary>An alias map carrying the given canonical filters and their configured colours.
    /// </summary>
    public static AliasMap Aliases(params (string Canonical, string Color)[] filters)
        => new(
            filters.ToDictionary(
                entry => entry.Canonical,
                entry => new FilterSetting { Color = entry.Color },
                StringComparer.Ordinal),
            new EquipmentSettings());

    public static AliasMap NoAliases() => Aliases();

    public static StatsOverview Overview(
        double integrationSeconds = 360_000d,
        int targetCount = 4,
        int totalFrames = 1200,
        int rigSessionCount = 24,
        DateOnly? first = null,
        DateOnly? last = null)
        => new(
            integrationSeconds,
            targetCount,
            totalFrames,
            rigSessionCount,
            first ?? new DateOnly(2023, 3, 10),
            last ?? new DateOnly(2025, 6, 2));

    public static EquipmentComboMetrics Combo(
        string telescope = "RC8",
        string camera = "ASI2600MM",
        double? medianHfr = 2.4d,
        double? medianEccentricity = 0.42d,
        double? medianFwhm = 2.1d,
        double? medianGuiding = 0.55d,
        bool grouped = false,
        IReadOnlyList<EquipmentFilterMetrics>? breakdown = null)
        => new(
            telescope,
            camera,
            FrameCount: 400,
            IntegrationSeconds: 120_000d,
            AvgSessionSeconds: 5_000d,
            MedianHfr: medianHfr,
            BestHfr: 1.9d,
            MedianEccentricity: medianEccentricity,
            MedianFwhm: medianFwhm,
            FwhmFrameCount: 380,
            MedianGuidingRmsArcsec: medianGuiding,
            MadHfr: 0.2d,
            MadEccentricity: 0.03d,
            MadFwhm: 0.15d,
            Grouped: grouped,
            FilterBreakdown: breakdown ?? [new EquipmentFilterMetrics("Ha", 200, 60_000d, 2.3d, 1.9d, 0.41d, 2.0d, 190)]);

    public static EquipmentInventoryItem Inventory(string name, bool grouped = false)
        => new(name, 400, 120_000d, 12, 3, 5_000d, 2.1d, 380, 0.55d, grouped);

    public static DataQualityStats Quality()
        => new(
            AvgHfr: 2.4d,
            AvgHfrArcsec: 1.8d,
            BestHfr: 1.7d,
            BestHfrArcsec: 1.2d,
            AvgEccentricity: 0.44d,
            UnscaledFrameCount: 17,
            EccentricityExcludedCount: 5,
            EccentricitySource: "HFRStDev",
            HfrPixelHistogram: [new HfrBucket("1.0", 1.0d, 1.5d, 30), new HfrBucket("1.5", 1.5d, 2.0d, 0)],
            HfrArcsecHistogram: [new HfrBucket("1.0", 1.0d, 1.5d, 12), new HfrBucket("8.0+", 8.0d, null, 1)]);

    /// <summary>One rig's guiding record (spec 12.5's Guiding section), every figure defaulted so a
    /// test spells only the column it is pinning.</summary>
    public static GuidingRig Rig(
        string telescope = "RC8",
        int sessions = 12,
        int gated = 0,
        double hours = 31.44d,
        double? rmsTotal = 0.72d,
        double? rmsRa = 0.48d,
        double? rmsDec = 0.54d,
        double? filtered = 0.61d,
        double? ratio = 1.1d,
        double? settle = 3.2d,
        IReadOnlyList<int>? exposures = null)
        => new(
            telescope,
            sessions,
            gated,
            hours,
            rmsTotal,
            rmsRa,
            rmsDec,
            filtered,
            ratio,
            settle,
            exposures ?? [1000, 2000]);

    /// <summary>One (rig, altitude band) bucket.</summary>
    public static GuidingAltitudeBandRow Band(
        string telescope = "RC8",
        GuidingAltitudeBand band = GuidingAltitudeBand.Above60,
        int sessions = 4,
        double? rmsTotal = 0.50d,
        double? rmsRa = 0.33d,
        double? rmsDec = 0.38d)
        => new(telescope, band, sessions, rmsTotal, rmsRa, rmsDec);

    /// <summary>A guiding section with the cross-rig baselines built from the rigs given, the way
    /// <c>GuidingStatsQuery</c> builds them, so a grading case and the shipped query agree about
    /// what the baseline of a set of rigs is.</summary>
    public static GuidingStats Guiding(
        int unmapped = 0,
        IReadOnlyList<GuidingRig>? rigs = null,
        IReadOnlyList<GuidingAltitudeBandRow>? bands = null)
    {
        var rows = rigs ?? [Rig()];
        return new GuidingStats(
            unmapped,
            rows,
            bands ?? [Band()],
            new GuidingBaselines(
                MetricBaseline.Of(rows.Select(rig => rig.RmsTotalArcsec)),
                MetricBaseline.Of(rows.Select(rig => rig.RmsRaArcsec)),
                MetricBaseline.Of(rows.Select(rig => rig.RmsDecArcsec))));
    }

    /// <summary>The bare guiding section a fresh library answers with.</summary>
    public static GuidingStats NoGuiding(int unmapped = 0) => Guiding(unmapped, [], []);

    /// <summary>
    /// A populated response. Every list is overridable so a test can pin one section without
    /// spelling the other twelve.
    /// </summary>
    public static StatsResponse Sample(
        StatsOverview? overview = null,
        IReadOnlyList<EquipmentInventoryItem>? cameras = null,
        IReadOnlyList<EquipmentInventoryItem>? telescopes = null,
        IReadOnlyList<EquipmentComboMetrics>? performance = null,
        IReadOnlyList<FilterUsageEntry>? filterUsage = null,
        IReadOnlyList<TopTargetEntry>? topTargets = null,
        IReadOnlyList<TimelineEntry>? monthly = null,
        IReadOnlyList<TimelineEntry>? weekly = null,
        IReadOnlyList<TimelineEntry>? daily = null,
        IReadOnlyDictionary<DateOnly, int>? rigsPerNight = null,
        DataQualityStats? quality = null,
        StorageStats? storage = null,
        IReadOnlyList<IngestEntry>? ingest = null,
        GuidingStats? guiding = null)
        => new(
            overview ?? Overview(),
            cameras ?? [Inventory("ASI2600MM")],
            telescopes ?? [Inventory("RC8")],
            performance ?? [Combo()],
            filterUsage ?? [new FilterUsageEntry("Ha", 60_000d), new FilterUsageEntry("OIII", 90_000d)],
            topTargets ?? [new TopTargetEntry("M 31", 120_000d), new TopTargetEntry("NGC 7000", 60_000d)],
            monthly ?? [new TimelineEntry("2025-05", 36_000d), new TimelineEntry("2025-06", 18_000d)],
            weekly ?? [new TimelineEntry("2025-W20", 18_000d), new TimelineEntry("2025-W22", 18_000d)],
            daily ?? [new TimelineEntry("2025-05-14", 18_000d), new TimelineEntry("2025-06-02", 18_000d)],
            rigsPerNight ?? new Dictionary<DateOnly, int>
            {
                [new DateOnly(2025, 5, 14)] = 1,
                [new DateOnly(2025, 6, 2)] = 2,
            },
            quality ?? Quality(),
            storage ?? new StorageStats(1_500_000_000L, 40_000_000L, 9_000_000L),
            ingest ?? [new IngestEntry(new DateOnly(2025, 6, 1), 120), new IngestEntry(new DateOnly(2025, 6, 2), 45)],
            guiding ?? Guiding());

    /// <summary>An empty library: no frames, no equipment, no periods.</summary>
    public static StatsResponse Empty() => new(
        new StatsOverview(0d, 0, 0, 0, null, null),
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        new Dictionary<DateOnly, int>(),
        new DataQualityStats(null, null, null, null, null, 0, null, null, [], []),
        new StorageStats(0, 0, 0),
        [],
        NoGuiding());

    public static StatisticsViewModel Create(
        Func<StatsResponse>? loadStats = null,
        Func<DateOnly, DateOnly, IReadOnlyList<CalendarEntry>>? loadCalendar = null,
        Func<GeneralSettings>? general = null,
        Func<AliasMap>? aliases = null,
        Action? invalidate = null,
        ScanStatusService? scanStatus = null,
        Func<DateOnly>? today = null,
        DerivedDataSource? derivedData = null)
    {
        var page = new StatisticsViewModel(
            loadStats ?? (() => Sample()),
            loadCalendar ?? ((_, _) => []),
            general ?? WithCoordinates,
            aliases ?? NoAliases,
            invalidate,
            scanStatus,
            post: action => action(),
            today: today ?? (() => Today),
            // Phase 15B fixer F2. Null unless a test hands one over, so every existing case builds
            // the page it built before and only the cases that name the notification follow it.
            subscribeDerivedDataChanged: derivedData is null ? null : derivedData.Subscribe,
            unsubscribeDerivedDataChanged: derivedData is null ? null : derivedData.Unsubscribe);

        Settle(page);
        return page;
    }

    /// <summary>
    /// Joins the page's load and the calendar load it starts. Two rounds, because the calendar's
    /// own task is created inside the page's post callback and so does not exist until the first
    /// has finished. In a harness helper rather than in a test method deliberately: a test that
    /// asserts the off-UI-thread rule awaits <c>SettleAsync</c> instead (TRACKING section 2
    /// item 8).
    /// </summary>
    public static void Settle(StatisticsViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            page.Quiesce(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>The awaiting form, for the tests that assert the load never blocks.</summary>
    public static async Task SettleAsync(StatisticsViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            if (page.PendingLoad is { } load)
            {
                await load.ConfigureAwait(false);
            }

            if (page.Calendar.PendingLoad is { } calendar)
            {
                await calendar.ConfigureAwait(false);
            }
        }
    }
}
