using System.Globalization;
using System.Text.Json;
using GalactiLog.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Tests;

/// <summary>
/// The deterministic database fixture of spec 18.2: 6 targets, 40 sessions, 900 frames, spanning
/// two years, across two rigs and five filters, with totals exposed as constants so an
/// aggregation assertion names <c>LibrarySeeder.FrameCount</c> rather than a magic number.
/// </summary>
/// <remarks>
/// Nothing here is random and nothing is read from disk: the plan is computed once from the
/// generation rules below, so <see cref="Targets"/> can be asserted against without seeding, and
/// two seeded databases are byte-comparable. Phases 6 and 9 reuse this seeder; do not fork a
/// second one.
/// </remarks>
public static class LibrarySeeder
{
    public const int TargetCount = 6;
    public const int SessionCount = 40;
    public const int FrameCount = 900;

    /// <summary>Every seeded frame carries the same exposure, so integration totals are exact.</summary>
    public const double ExposureSeconds = 300d;

    public const double TotalIntegrationSeconds = FrameCount * ExposureSeconds;

    /// <summary>Session dates are <see cref="FirstSessionDate"/> plus a multiple of this, which
    /// puts the 40th session 702 days out: two calendar years, just under two full years.</summary>
    private const int SessionStrideDays = 18;

    public static readonly DateOnly FirstSessionDate = new(2024, 1, 5);
    public static readonly DateOnly LastSessionDate = FirstSessionDate.AddDays((SessionCount - 1) * SessionStrideDays);

    /// <summary>The five optical filters, in the order the generator cycles them.</summary>
    public static readonly IReadOnlyList<string> Filters = ["L", "R", "G", "B", "Ha"];

    /// <summary>The two rigs, as the raw (telescope, camera) strings stored on the frames.</summary>
    public static readonly IReadOnlyList<(string Telescope, string Camera)> Rigs =
    [
        ("RC8", "ASI2600MM"),
        ("FRA600", "ASI294MC"),
    ];

    // ---- Phase 6 additive columns ------------------------------------------------------
    //
    // Phase 6's detail queries need known values for arcsec_per_pixel, eccentricity_source,
    // guiding_rms_arcsec, sensor_temp and camera_gain, which the Phase 5 generator left null
    // (ruling Q24). Every value below is new: no existing constant changes, no frame count or
    // integration total moves.

    /// <summary>Plate scale of rig 0 (<c>RC8</c> / <c>ASI2600MM</c>). The two rigs differ so a
    /// target imaged on both exercises the per-frame conversion rather than one constant.</summary>
    public const double ArcsecPerPixelRig0 = 0.62;

    /// <summary>Plate scale of rig 1 (<c>FRA600</c> / <c>ASI294MC</c>).</summary>
    public const double ArcsecPerPixelRig1 = 1.58;

    /// <summary>Frame <c>j</c> of a session carries <c>GuidingRmsBase + (j % 4) * 0.05</c>
    /// arcseconds, rounded to two places.</summary>
    public const double GuidingRmsBase = 0.45;

    /// <summary>Frame <c>j</c> of a session carries <c>SensorTempBase + (j % 3) * 0.5</c>
    /// degrees, rounded to two places.</summary>
    public const double SensorTempBase = -10.0;

    /// <summary>Every seeded frame carries the same gain, so a gain assertion is exact.</summary>
    public const int Gain = 100;

    /// <summary>The <c>eccentricity_source</c> of every seeded frame except the minority inside
    /// <see cref="SessionWithMinorityEccentricitySource"/>, and therefore the modal source of
    /// every seeded target.</summary>
    public const string EccentricitySourceHeader = "header";

    /// <summary>The non-modal <c>eccentricity_source</c> a known minority of frames carries, so
    /// an excluded count is a non-zero known number rather than always zero.</summary>
    public const string EccentricitySourceMinority = "ellipticity";

    /// <summary>The one seeded session whose frames carry no <c>arcsec_per_pixel</c>, so the
    /// arcsecond excluded count is non-zero and known. It is session index 7, which belongs to
    /// <c>Targets[1]</c> (M 42) and holds <see cref="FramesWithoutPlateScale"/> frames.</summary>
    public static readonly DateOnly SessionWithoutPlateScale =
        FirstSessionDate.AddDays(SessionWithoutPlateScaleIndex * SessionStrideDays);

    /// <summary>Frames in <see cref="SessionWithoutPlateScale"/>, all of them without a plate
    /// scale.</summary>
    public const int FramesWithoutPlateScale = 23;

    /// <summary>The one seeded session holding frames whose <c>eccentricity_source</c> is not the
    /// modal one. It is session index 3, which belongs to <c>Targets[3]</c> (M 45).</summary>
    public static readonly DateOnly SessionWithMinorityEccentricitySource =
        FirstSessionDate.AddDays(MinorityEccentricitySourceSessionIndex * SessionStrideDays);

    /// <summary>Frames inside <see cref="SessionWithMinorityEccentricitySource"/> that carry
    /// <see cref="EccentricitySourceMinority"/>: every fifth frame of a 23 frame session.</summary>
    public const int FramesWithMinorityEccentricitySource = 5;

    // ---- Phase 6 Task 2 additive columns -----------------------------------------------
    //
    // Spec 12.4's expanded session card shows the median airmass, ambient temperature and
    // humidity, which the Phase 5 generator and Task 1's extension both left null (Task 1
    // deviation D6). Additive under the Q24 constraints: no existing constant changes value and
    // no frame count or integration total moves.

    /// <summary>Frame <c>j</c> of a session carries <c>AirmassBase + (j % 5) * 0.05</c>, rounded
    /// to two places, so a session holds five distinct values and a median is not the only
    /// value.</summary>
    public const double AirmassBase = 1.05;

    /// <summary>Frame <c>j</c> of a session carries <c>AmbientTempBase - (j % 4) * 0.5</c>
    /// degrees, rounded to two places: a night that cools as it goes.</summary>
    public const double AmbientTempBase = 8.0;

    /// <summary>Frame <c>j</c> of a session carries <c>HumidityBase + (j % 3) * 2.5</c>
    /// percent, rounded to two places.</summary>
    public const double HumidityBase = 55.0;

    private const int SessionWithoutPlateScaleIndex = 7;
    private const int MinorityEccentricitySourceSessionIndex = 3;

    // ---- Phase 9 Task 2 additive totals -------------------------------------------------
    //
    // Spec 12.5's equipment inventory and equipment performance sections are per rig, and the
    // rig a session belongs to is a private rule of the generator below, so a stats assertion
    // could not name its expected frame count without re-implementing that rule. These three
    // lists are computed from the same plan every other figure comes from. Additive: no existing
    // constant changes value, no frame count or integration total moves, and nothing new is
    // written to the database.

    /// <summary>LIGHT frames per rig, indexed by <see cref="Rigs"/>. Sums to
    /// <see cref="FrameCount"/>.</summary>
    public static IReadOnlyList<int> FramesPerRig => SeedPlan.FramesPerRig;

    /// <summary>Distinct <c>session_date</c> values per rig, indexed by <see cref="Rigs"/>. This
    /// is the inventory's "nights" column, and the two rigs never share a night, so the two
    /// counts sum to <see cref="SessionCount"/>.</summary>
    public static IReadOnlyList<int> NightsPerRig => SeedPlan.NightsPerRig;

    /// <summary>Distinct resolved targets per rig, indexed by <see cref="Rigs"/>.</summary>
    public static IReadOnlyList<int> TargetsPerRig => SeedPlan.TargetsPerRig;

    /// <summary>One seeded target and the totals its frames add up to.</summary>
    public sealed record SeededTarget(
        Guid Id,
        string PrimaryName,
        string? CatalogId,
        string? CommonName,
        string? ObjectType,
        string ObjectCategory,
        IReadOnlyList<string> Aliases,
        int FrameCount,
        int SessionCount,
        DateOnly FirstSession,
        DateOnly LastSession)
    {
        public double IntegrationSeconds => FrameCount * ExposureSeconds;
    }

    private static readonly (string PrimaryName, string? CatalogId, string? CommonName, string? ObjectType, string Category, string[] Aliases)[] TargetDefinitions =
    [
        ("M 31", "M 31", "Andromeda Galaxy", "G", "Galaxy", ["NGC 224", "Andromeda"]),
        ("M 42", "M 42", "Orion Nebula", "HII", "Emission Nebula", ["NGC 1976", "Great Orion Nebula"]),
        ("M 13", "M 13", "Hercules Cluster", "GlC", "Globular Cluster", ["NGC 6205"]),
        ("M 45", "M 45", "Pleiades", "OpC", "Open Cluster", ["Seven Sisters"]),
        ("M 27", "M 27", "Dumbbell Nebula", "PN", "Planetary Nebula", ["NGC 6853"]),
        ("NGC 6960", "NGC 6960", "Western Veil", "SNR", "Supernova Remnant", []),
    ];

    private static readonly Plan SeedPlan = BuildPlan();

    /// <summary>The seeded targets in generation order, with their exact totals.</summary>
    public static IReadOnlyList<SeededTarget> Targets => SeedPlan.Targets;

    /// <summary>Writes the deterministic library into an already-migrated database.</summary>
    public static void Seed(string connectionString)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Targets.AddRange(SeedPlan.TargetRows.Select(CloneTarget));
        context.Images.AddRange(SeedPlan.ImageRows.Select(CloneImage));
        context.SaveChanges();
    }

    /// <summary>Inserts one extra target. The caller shapes it through <paramref name="configure"/>
    /// (object type, aliases, merged_into_id, ...).</summary>
    public static Target AddTarget(string connectionString, string primaryName, Action<Target>? configure = null)
    {
        var target = new Target
        {
            Id = Guid.NewGuid(),
            PrimaryName = primaryName,
            Aliases = "[]",
        };
        configure?.Invoke(target);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Targets.Add(target);
        context.SaveChanges();
        return target;
    }

    /// <summary>Inserts one extra frame with chosen metric values. Defaults to a LIGHT frame with
    /// the seeder's exposure and a unique file path; <paramref name="configure"/> overrides any
    /// column, which is how the metric-range and grouping tests build their own small cases.</summary>
    public static Image AddFrame(
        string connectionString,
        Guid? targetId,
        DateOnly sessionDate,
        Action<Image>? configure = null)
    {
        var id = Guid.NewGuid();
        var image = new Image
        {
            Id = id,
            FilePath = $@"C:\GalactiLogFixture\extra\{id:N}.fits",
            FileName = $"{id:N}.fits",
            SessionDate = sessionDate,
            CaptureDate = sessionDate.ToDateTime(new TimeOnly(22, 0)),
            ResolvedTargetId = targetId,
            ImageType = "LIGHT",
            ExposureTime = ExposureSeconds,
        };
        configure?.Invoke(image);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Images.Add(image);
        context.SaveChanges();
        return image;
    }

    /// <summary>Inserts one <c>merge_candidates</c> row. Defaults to a pending orphan with no
    /// suggested target and a zero score; <paramref name="configure"/> overrides any column,
    /// which is how the dedup, merge and candidate-list tests build their own cases.</summary>
    public static MergeCandidate AddMergeCandidate(
        string connectionString, string sourceName, Action<MergeCandidate>? configure = null)
    {
        var candidate = new MergeCandidate
        {
            Id = Guid.NewGuid(),
            SourceName = sourceName,
            Status = "pending",
            Method = "orphan",
            SimilarityScore = 0,
            CreatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(candidate);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.MergeCandidates.Add(candidate);
        context.SaveChanges();
        return candidate;
    }

    /// <summary>Inserts one <c>session_notes</c> row and returns it, so a merge test can seed a
    /// colliding date in one line. Unique on (target_id, session_date) per spec 5.9.</summary>
    public static SessionNote AddSessionNote(
        string connectionString, Guid targetId, DateOnly sessionDate, string notes)
    {
        var note = new SessionNote
        {
            Id = Guid.NewGuid(),
            TargetId = targetId,
            SessionDate = sessionDate,
            Notes = notes,
            UpdatedAt = DateTime.UtcNow,
        };

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.SessionNotes.Add(note);
        context.SaveChanges();
        return note;
    }

    /// <summary>A raw_headers document holding just an OBJECT card, for the unresolved grouping
    /// cases.</summary>
    public static string RawHeadersWithObject(string objectName)
        => JsonSerializer.Serialize(new Dictionary<string, object> { ["OBJECT"] = objectName });

    // ---- the plan ----------------------------------------------------------------------
    //
    // Session i (0-based) belongs to target i % 6, so targets 0-3 get 7 sessions each and
    // targets 4-5 get 6, for 40. It holds 22 frames when i is even and 23 when i is odd, for
    // 900. Its rig is the target's own rig (rigs alternate by target index), except target 0,
    // which alternates rigs between its own sessions so at least one seeded group carries two.
    // Filters cycle so every session holds all five.

    private sealed record Plan(
        IReadOnlyList<SeededTarget> Targets,
        IReadOnlyList<Target> TargetRows,
        IReadOnlyList<Image> ImageRows,
        IReadOnlyList<int> FramesPerRig,
        IReadOnlyList<int> NightsPerRig,
        IReadOnlyList<int> TargetsPerRig);

    private static Plan BuildPlan()
    {
        var targetRows = new List<Target>(TargetCount);
        for (var t = 0; t < TargetCount; t++)
        {
            var (primaryName, catalogId, commonName, objectType, _, aliases) = TargetDefinitions[t];
            targetRows.Add(new Target
            {
                Id = TargetId(t),
                PrimaryName = primaryName,
                CatalogId = catalogId,
                CatalogIdNormalized = catalogId?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant(),
                CommonName = commonName,
                ObjectType = objectType,
                Aliases = JsonSerializer.Serialize(aliases),
            });
        }

        var images = new List<Image>(FrameCount);
        var perRigFrames = new int[Rigs.Count];
        var perRigNights = new HashSet<DateOnly>[Rigs.Count];
        var perRigTargets = new HashSet<Guid>[Rigs.Count];
        for (var r = 0; r < Rigs.Count; r++)
        {
            perRigNights[r] = [];
            perRigTargets[r] = [];
        }

        var perTargetFrames = new int[TargetCount];
        var perTargetSessions = new int[TargetCount];
        var firstSession = new DateOnly[TargetCount];
        var lastSession = new DateOnly[TargetCount];

        for (var i = 0; i < SessionCount; i++)
        {
            var targetIndex = i % TargetCount;
            var sessionDate = FirstSessionDate.AddDays(i * SessionStrideDays);
            var sessionOfTarget = i / TargetCount;
            var rigIndex = targetIndex == 0 ? sessionOfTarget % 2 : targetIndex % 2;
            var rig = Rigs[rigIndex];
            var framesInSession = i % 2 == 0 ? 22 : 23;
            var plateScale = i == SessionWithoutPlateScaleIndex
                ? (double?)null
                : rigIndex == 0 ? ArcsecPerPixelRig0 : ArcsecPerPixelRig1;
            var minorityEccentricitySource = i == MinorityEccentricitySourceSessionIndex;

            perTargetSessions[targetIndex]++;
            if (perTargetSessions[targetIndex] == 1)
            {
                firstSession[targetIndex] = sessionDate;
            }

            lastSession[targetIndex] = sessionDate;

            perRigFrames[rigIndex] += framesInSession;
            perRigNights[rigIndex].Add(sessionDate);
            perRigTargets[rigIndex].Add(TargetId(targetIndex));

            for (var j = 0; j < framesInSession; j++)
            {
                var index = images.Count;
                var name = $"{TargetDefinitions[targetIndex].PrimaryName.Replace(" ", "", StringComparison.Ordinal)}-{index:D4}.fits";
                images.Add(new Image
                {
                    Id = FrameId(index),
                    FilePath = $@"C:\GalactiLogFixture\{sessionDate:yyyy-MM-dd}\{name}",
                    FileName = name,
                    SessionDate = sessionDate,
                    CaptureDate = sessionDate.ToDateTime(new TimeOnly(21, 0)).AddMinutes(5 * j),
                    ResolvedTargetId = TargetId(targetIndex),
                    ImageType = "LIGHT",
                    ExposureTime = ExposureSeconds,
                    FilterUsed = Filters[(i + j) % Filters.Count],
                    Telescope = rig.Telescope,
                    Camera = rig.Camera,
                    MedianHfr = Math.Round(2.0 + (j % 5) * 0.1, 2),
                    Fwhm = Math.Round(3.0 + (j % 4) * 0.25, 2),
                    Eccentricity = Math.Round(0.30 + (j % 3) * 0.05, 2),
                    EccentricitySource = minorityEccentricitySource && j % 5 == 0
                        ? EccentricitySourceMinority
                        : EccentricitySourceHeader,
                    ArcsecPerPixel = plateScale,
                    GuidingRmsArcsec = Math.Round(GuidingRmsBase + (j % 4) * 0.05, 2),
                    SensorTemp = Math.Round(SensorTempBase + (j % 3) * 0.5, 2),
                    CameraGain = Gain,
                    Airmass = Math.Round(AirmassBase + (j % 5) * 0.05, 2),
                    AmbientTemp = Math.Round(AmbientTempBase - (j % 4) * 0.5, 2),
                    Humidity = Math.Round(HumidityBase + (j % 3) * 2.5, 2),
                    DetectedStars = 800 + j,
                    RawHeaders = RawHeadersWithObject(TargetDefinitions[targetIndex].PrimaryName),
                });
                perTargetFrames[targetIndex]++;
            }
        }

        var targets = new List<SeededTarget>(TargetCount);
        for (var t = 0; t < TargetCount; t++)
        {
            var (primaryName, catalogId, commonName, objectType, category, aliases) = TargetDefinitions[t];
            targets.Add(new SeededTarget(
                TargetId(t),
                primaryName,
                catalogId,
                commonName,
                objectType,
                category,
                aliases,
                perTargetFrames[t],
                perTargetSessions[t],
                firstSession[t],
                lastSession[t]));
        }

        return new Plan(
            targets,
            targetRows,
            images,
            perRigFrames,
            [.. perRigNights.Select(nights => nights.Count)],
            [.. perRigTargets.Select(rigTargets => rigTargets.Count)]);
    }

    private static Guid TargetId(int index)
        => Guid.ParseExact($"10000000-0000-0000-0000-{index.ToString("D12", CultureInfo.InvariantCulture)}", "D");

    private static Guid FrameId(int index)
        => Guid.ParseExact($"20000000-0000-0000-0000-{index.ToString("D12", CultureInfo.InvariantCulture)}", "D");

    // The plan's entity instances are the template, not the rows themselves: a DbContext takes
    // ownership of what it tracks, so each Seed call gets its own copies and a second seeded
    // database cannot be polluted by the first one's change tracker.
    private static Target CloneTarget(Target source) => new()
    {
        Id = source.Id,
        PrimaryName = source.PrimaryName,
        CatalogId = source.CatalogId,
        CatalogIdNormalized = source.CatalogIdNormalized,
        CommonName = source.CommonName,
        ObjectType = source.ObjectType,
        Aliases = source.Aliases,
    };

    private static Image CloneImage(Image source) => new()
    {
        Id = source.Id,
        FilePath = source.FilePath,
        FileName = source.FileName,
        SessionDate = source.SessionDate,
        CaptureDate = source.CaptureDate,
        ResolvedTargetId = source.ResolvedTargetId,
        ImageType = source.ImageType,
        ExposureTime = source.ExposureTime,
        FilterUsed = source.FilterUsed,
        Telescope = source.Telescope,
        Camera = source.Camera,
        MedianHfr = source.MedianHfr,
        Fwhm = source.Fwhm,
        Eccentricity = source.Eccentricity,
        // Every column the generator sets has to be cloned. A forgotten one silently seeds
        // nulls, which is the single most likely defect in this fixture.
        EccentricitySource = source.EccentricitySource,
        ArcsecPerPixel = source.ArcsecPerPixel,
        GuidingRmsArcsec = source.GuidingRmsArcsec,
        SensorTemp = source.SensorTemp,
        CameraGain = source.CameraGain,
        Airmass = source.Airmass,
        AmbientTemp = source.AmbientTemp,
        Humidity = source.Humidity,
        DetectedStars = source.DetectedStars,
        RawHeaders = source.RawHeaders,
    };
}
