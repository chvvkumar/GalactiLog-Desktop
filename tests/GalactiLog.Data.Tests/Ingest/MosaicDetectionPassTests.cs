using System.Text.Json;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Phase 18 Task 3. Spec 7.7's pass over the plan's fixture, seeded straight to the catalogue as a
// scan from before Phase 18 left it: panel_label and the geometry columns null, the headers in
// raw_headers. NGC 7000's four panels resolve to one target on a 2 by 2 grid one field apart (two
// nights in two filters); IC 1396 P1 is a lone first panel; Sh2-155's two panels share one
// position; North America Nebula carries no token.
public class MosaicDetectionPassTests : IDisposable
{
    private const double FieldArcmin = 100;   // NAXIS1 6000 at 1 arcsec per pixel
    private static readonly DetectionSettings Default = new(["Panel", "P"], 0, 0);

    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly MosaicRepository _repository;
    private readonly Guid _ngc7000;

    public MosaicDetectionPassTests()
    {
        _repository = new MosaicRepository(new DatabaseConnectionString(_db.ConnectionString));
        _ngc7000 = NewTarget("NGC 7000");
        var step = FieldArcmin / 60;
        var stepRa = step / Math.Cos(44.3 * Math.PI / 180);
        foreach (var (panel, ra, dec) in new[]
        {
            (1, 314.7, 44.3), (2, 314.7 + stepRa, 44.3), (3, 314.7, 44.3 + step), (4, 314.7 + stepRa, 44.3 + step),
        })
        {
            foreach (var day in new[] { 1, 2 })
            {
                foreach (var filter in new[] { "Ha", "OIII" })
                {
                    Frame(_ngc7000, day, $"NGC 7000 Panel {panel}", ra, dec, filter);
                }
            }
        }

        Frame(NewTarget("IC 1396"), 1, "IC 1396 P1", 324.7, 57.5);
        var sh2 = NewTarget("Sh2-155");
        Frame(sh2, 1, "Sh2-155 Panel 1", 344.2, 62.6);
        Frame(sh2, 1, "Sh2-155 Panel 2", 344.2, 62.6);
        var northAmerica = NewTarget("North America Nebula");
        Frame(northAmerica, 1, "North America Nebula", 314.7, 44.3);
        Frame(northAmerica, 3, "North America Nebula", ra: null, dec: null);
        Frame(_ngc7000, 1, "NGC 7000 Panel 1", 314.7, 44.3, imageType: "DARK");
    }

    public void Dispose() => _db.Dispose();

    private static DateOnly Night(int day) => new(2026, 3, day);

    private Guid NewTarget(string name) => LibrarySeeder.AddTarget(_db.ConnectionString, name).Id;

    private void Frame(Guid target, int day, string objectName, double? ra, double? dec, string filter = "Ha", string imageType = "LIGHT")
    {
        var headers = new Dictionary<string, object> { ["OBJECT"] = objectName, ["FOCALLEN"] = 800, ["XPIXSZ"] = 3.76 };
        if (ra is { } r) headers["RA"] = r;
        if (dec is { } d) headers["DEC"] = d;
        if (ra is not null) headers["NAXIS1"] = 6000;
        LibrarySeeder.AddFrame(_db.ConnectionString, target, Night(day), image =>
        {
            image.RawHeaders = JsonSerializer.Serialize(headers);
            image.ImageType = imageType;
            image.FilterUsed = filter;
            image.ArcsecPerPixel = 1.0;
        });
    }

    private MosaicDetectionResult Run(DetectionSettings? settings = null, Action<int, int, string>? report = null, CancellationToken ct = default)
        => new MosaicDetectionPass(_db.ConnectionString, settings ?? Default).Run(report ?? ((_, _, _) => { }), ct);

    private List<Image> Lights()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        return [.. context.Images.Where(image => image.ImageType == "LIGHT")];
    }

    // ---- step 0 ----------------------------------------------------------------------------

    [Fact]
    public void Step0_RelabelsEveryTokenFrame_AndBackfillsPositionsFromTheHeaders()
    {
        var result = Run();

        // 16 NGC 7000, 1 IC 1396 and 2 Sh2-155 frames carry a token; those and one North America
        // Nebula frame have RA and DEC.
        Assert.Equal(19, result.Relabelled);
        Assert.Equal(20, result.Backfilled);
        var frame = Lights().First(image => image.RawHeaders!.Contains("\"NGC 7000 Panel 3\"", StringComparison.Ordinal));
        Assert.Equal("Panel 3", frame.PanelLabel);
        Assert.Equal((314.7, Math.Round(44.3 + FieldArcmin / 60, 6), 6000), (frame.RaDeg!.Value, Math.Round(frame.DecDeg!.Value, 6), frame.WidthPx!.Value));
        Assert.All(Lights().Where(image => image.RawHeaders!.Contains("North America", StringComparison.Ordinal)),
            image => Assert.Null(image.PanelLabel));

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        var dark = context.Images.Single(image => image.ImageType == "DARK");
        Assert.Equal(((string?)null, (double?)null), (dark.PanelLabel, dark.RaDeg));
    }

    [Fact]
    public void ASecondRun_ChangesNothing_AndAFrameWithNoHeadersStaysNull()
    {
        Run();

        var second = Run();

        Assert.Equal((0, 0), (second.Relabelled, second.Backfilled));
        var headerless = Lights().Single(image => image.SessionDate == Night(3));
        Assert.Equal(((double?)null, (double?)null, (int?)null), (headerless.RaDeg, headerless.DecDeg, headerless.WidthPx));
    }

    [Fact]
    public void AKeywordChange_RelabelsTheFramesItReaches_AndDropsTheirSuggestion()
    {
        Run();

        var result = Run(Default with { Keywords = ["Panel"] });

        Assert.Equal(1, result.Relabelled);
        Assert.Null(Lights().Single(image => image.RawHeaders!.Contains("IC 1396", StringComparison.Ordinal)).PanelLabel);
        Assert.DoesNotContain("IC 1396", _repository.ListPending().Select(row => row.SuggestedName));
    }

    // Review fix 1: the relabel writes by primary key and only the rows whose label moved. A
    // trigger counts the UPDATEs that reach panel_label, independently of the pass's own count.
    [Fact]
    public void Relabel_WritesOnlyTheRowsWhoseLabelMoved()
    {
        Run();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            context.Database.ExecuteSqlRaw("""
                CREATE TABLE label_writes (n INTEGER NOT NULL);
                INSERT INTO label_writes VALUES (0);
                CREATE TRIGGER count_label_writes AFTER UPDATE OF panel_label ON images
                BEGIN UPDATE label_writes SET n = n + 1; END;
                """);
        }

        Run();
        Assert.Equal(0L, LabelWrites());

        Run(Default with { Keywords = ["Panel"] });
        Assert.Equal(1L, LabelWrites());
    }

    private long LabelWrites()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        return context.Database.SqlQueryRaw<long>("SELECT n AS Value FROM label_writes").AsEnumerable().Single();
    }

    // ---- suggestions -------------------------------------------------------------------------

    [Fact]
    public void Run_WritesTheFixturesSuggestions_WithTheirConfidence()
    {
        var result = Run();

        var pending = _repository.ListPending();
        Assert.Equal(new[] { "IC 1396", "NGC 7000", "Sh2-155" }, pending.Select(row => row.SuggestedName));
        Assert.Equal(3, result.SuggestionsWritten);
        var ngc = pending[1];
        Assert.Equal(("high", "both"), (ngc.Confidence, ngc.DiscoverySource));
        Assert.Equal(new[] { "Panel 1", "Panel 2", "Panel 3", "Panel 4" }, ngc.Panels.Select(panel => panel.Label));
        Assert.All(ngc.Panels, panel => Assert.Equal(new[] { Night(1), Night(2) }, panel.Dates));
        Assert.Equal("low", pending[0].Confidence);
        Assert.Contains(pending[2].Flags, flag => flag.StartsWith("Positions not distinct", StringComparison.Ordinal));
    }

    [Fact]
    public void ADismissedSuggestion_StaysAway_UntilANewNightIsShot()
    {
        Run();
        _repository.Dismiss(_repository.ListPending().Single(row => row.SuggestedName == "NGC 7000").Id);

        var again = Run();
        Assert.Equal(1, again.SkippedDismissed);
        Assert.DoesNotContain("NGC 7000", _repository.ListPending().Select(row => row.SuggestedName));

        Frame(_ngc7000, 5, "NGC 7000 Panel 1", 314.7, 44.3);
        Run();
        Assert.Contains("NGC 7000", _repository.ListPending().Select(row => row.SuggestedName));
    }

    [Fact]
    public void AnAcceptedSuggestion_IsNotOfferedAgain()
    {
        Run();
        _repository.Accept(_repository.ListPending().Single(row => row.SuggestedName == "IC 1396").Id, ["Panel 1"]);

        var again = Run();

        Assert.Equal(1, again.SkippedExisting);
        Assert.Equal(new[] { "NGC 7000", "Sh2-155" }, _repository.ListPending().Select(row => row.SuggestedName));
    }

    // ---- progress and cancellation -------------------------------------------------------------

    [Fact]
    public void Run_ReportsTheFourSteps_InOrder()
    {
        var steps = new List<(int, int, string)>();

        Run(report: (step, total, message) => steps.Add((step, total, message)));

        Assert.Equal(
            new[] { (1, 4, "Relabelling frames"), (2, 4, "Filling positions"), (3, 4, "Grouping candidates"), (4, 4, "Writing suggestions") },
            steps);
    }

    [Fact]
    public void ACancellationBetweenSteps_StopsThePass_AndLeavesThePreviousListInPlace()
    {
        Run();
        var before = _repository.ListPending().Select(row => row.Id).ToList();
        using var cts = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => Run(
            report: (step, _, _) => { if (step == 3) cts.Cancel(); },
            ct: cts.Token));

        Assert.Equal(before, _repository.ListPending().Select(row => row.Id));
    }

    [Fact]
    public void LabelFor_IsTheTokenRulesLabel_OrNull()
    {
        Assert.Equal("Panel 3", MosaicDetectionPass.LabelFor("NGC 7000 P3", ["Panel", "P"]));
        Assert.Equal("Panel 1-2", MosaicDetectionPass.LabelFor("IC1805_1-2", []));
        Assert.Null(MosaicDetectionPass.LabelFor("North America Nebula", ["Panel", "P"]));
        Assert.Null(MosaicDetectionPass.LabelFor(null, ["Panel"]));
    }
}
