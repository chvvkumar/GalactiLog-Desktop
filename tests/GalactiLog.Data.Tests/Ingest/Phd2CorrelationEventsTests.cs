using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// The one implementation of spec 10.9's correlation-time rows, extracted from the two callers that
// each carried a copy of it (phase review P2-4, design-lessons rule 1). The two callers' own wiring
// is pinned in Phd2ScanCorrelationTests and Phd2CorrelationRunnerTests; what is tested here is what
// the extraction made a single fact rather than two: the message, the subtraction, and the guard.
public class Phd2CorrelationEventsTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Phd2CorrelationResult Result(
        int filled = 0, int nights = 0,
        IReadOnlyList<string>? timezoneUnset = null,
        IReadOnlyList<string>? pixelScaleMissing = null)
        => new(nights, 0, filled, 0, 0, [], 0, timezoneUnset ?? [], pixelScaleMissing ?? [], false);

    private static Phd2PassResult Ingest(
        IReadOnlyList<string>? timezoneUnset = null, IReadOnlyList<string>? pixelScaleMissing = null)
        => new(0, 0, 0, 0, 0, 0, [], [], timezoneUnset ?? [], pixelScaleMissing ?? []);

    private List<ActivityEvent> EventsOfType(string eventType)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        return context.ActivityEvents.Where(e => e.EventType == eventType).OrderBy(e => e.Id).ToList();
    }

    private static JsonElement Details(ActivityEvent row)
        => JsonDocument.Parse(row.Details!).RootElement.Clone();

    [Theory]
    [InlineData(1, 1, "filled 1 frame guiding value over 1 night")]
    [InlineData(0, 2, "filled 0 frame guiding values over 2 nights")]
    [InlineData(3, 0, "filled 3 frame guiding values over 0 nights")]
    public void Describe_AgreesWithItselfOnEveryPlural(int filled, int nights, string expected)
    {
        // The sentence is the phd2_correlation_complete message, the job's recent-list summary and
        // the pass's closing progress line. It used to exist three times, so the flyout and the
        // feed could disagree about one pass while every test stayed green. A failure here is
        // cosmetic on its own and is pinned because a second copy is how the drift starts.
        Assert.Contains(expected, Phd2CorrelationEvents.Describe(Result(filled, nights)), StringComparison.Ordinal);
    }

    [Fact]
    public void OutOfAScan_TheTwoWarningsAreNotSubtracted()
    {
        // No ingest runs beside the out-of-scan re-run, so there is no duplicate to remove and the
        // correlation says everything it has to say. A failure looks like a user who saved a
        // mapping being told nothing about the profiles that mapping still cannot resolve.
        Phd2CorrelationEvents.Emit(
            _db.ConnectionString,
            Result(timezoneUnset: ["RigZ"], pixelScaleMissing: ["AsiRig"]),
            Phd2CorrelationTriggers.SettingsChange, parentId: null);

        Assert.Single(EventsOfType("phd2_timezone_unset"));
        Assert.Single(EventsOfType("phd2_pixel_scale_missing"));
    }

    [Fact]
    public void InAScan_OnlyTheProfilesTheIngestDidNotName_AreNamedAgain()
    {
        // Spec 10.9 raises each of these two warnings once per pass, and a scan runs two passes
        // over one corpus. The subtraction is what keeps that true across both without losing the
        // correlation's own contribution, which is the profiles the STORED corpus held back from a
        // night it visited rather than the profiles this run's ingest happened to open.
        //
        // A failure in one direction leaves two identical-looking warnings under one collapsed
        // entry; in the other it drops the correlation's copy entirely, and on a rescan that
        // delta-skips every log that copy is the only one there is.
        Phd2CorrelationEvents.Emit(
            _db.ConnectionString,
            Result(timezoneUnset: ["RigY", "RigZ"], pixelScaleMissing: ["AsiRig"]),
            Phd2CorrelationTriggers.Scan, parentId: null,
            ingest: Ingest(timezoneUnset: ["RigZ"], pixelScaleMissing: ["AsiRig"]));

        // RigZ was named by the ingest already; RigY was not.
        var row = Assert.Single(EventsOfType("phd2_timezone_unset"));
        Assert.Equal(
            ["RigY"],
            Details(row).GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!).ToArray());

        // Nothing left to say about the pixel scale, so no row at all rather than an empty one.
        Assert.Empty(EventsOfType("phd2_pixel_scale_missing"));
    }

    [Fact]
    public void AFailureWritingTheRows_IsWarnedAboutAndNeverThrown()
    {
        // Spec 10.9's closing sentence: by the time these rows are written the guiding values are
        // already committed, so losing the note about them is not worth losing the work. A failure
        // looks like a busy SQLite handle turning a completed frame scan into a failed one over a
        // secondary guiding column, which is exactly what the catch three lines above the emit in
        // ScanCoordinator exists to prevent.
        var warnings = new List<string>();
        var unopenable = $"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no", "such.db")}";

        Phd2CorrelationEvents.Emit(
            unopenable, Result(), Phd2CorrelationTriggers.Scan, parentId: null, warn: warnings.Add);
        Phd2CorrelationEvents.EmitFailed(
            unopenable, Phd2CorrelationTriggers.Scan, "something went wrong", parentId: null,
            warn: warnings.Add);

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, message => Assert.Contains("correlation activity events", message, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDetailsKeyOfEveryRow_IsSnakeCase()
    {
        // The house rule, checked at the one place the documents are now built rather than at two.
        Phd2CorrelationEvents.Emit(
            _db.ConnectionString,
            new Phd2CorrelationResult(1, 2, 3, 4, 5, ["RigOne"], 1, ["RigZ"], ["AsiRig"], false),
            Phd2CorrelationTriggers.Scan, parentId: null);
        Phd2CorrelationEvents.EmitFailed(
            _db.ConnectionString, Phd2CorrelationTriggers.Scan, "boom", parentId: null);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        var rows = context.ActivityEvents.ToList();
        Assert.Equal(5, rows.Count);
        foreach (var key in rows.SelectMany(row => Details(row).EnumerateObject()).Select(p => p.Name))
        {
            Assert.Equal(key.ToLowerInvariant(), key);
            Assert.DoesNotContain(' ', key);
            Assert.DoesNotContain('-', key);
        }
    }
}
