using System.Text;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 10.9's two new rows, Phase 14B Task 5: orphan_prune_limited and orphan_prune_forced. Both
// scan category, both warning, both parented to the scan_started row like every other scan
// sub-event. This is tests/**, so writing fixture files here is fine.
public class OrphanPruneEventsTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly SettingsStore _settings;
    private readonly TargetResolver _resolver;
    private readonly ScanRunRepository _scanRuns;
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-orphanevents-").FullName;

    public OrphanPruneEventsTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _resolver = new TargetResolver(
            _db.ConnectionString, StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(new ThrowingHandler()), new SesameClient(new ThrowingHandler()));
        _scanRuns = new ScanRunRepository(_db.ConnectionString);
        _settings.SaveGeneral(new GeneralSettings { ScanRoots = [_root], ScanFilters = ScanFilterConfig.Empty });
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private const int FitsBlockSize = 2880;

    private static byte[] MinimalFits()
    {
        var cards = new[]
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    0",
            "IMAGETYP= 'LIGHT   '",
            "DATE-OBS= '2025-03-15T02:00:00'",
            "EXPTIME =                300.0",
            "END",
        };
        var text = new StringBuilder();
        foreach (var card in cards) text.Append(card.PadRight(80));
        var blocks = (text.Length + FitsBlockSize - 1) / FitsBlockSize;
        return Encoding.ASCII.GetBytes(text.ToString().PadRight(blocks * FitsBlockSize));
    }

    private string WriteFrame(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, MinimalFits());
        return path;
    }

    private ScanCoordinator MakeCoordinator()
        => new(_db.ConnectionString, _settings, _resolver, _scanRuns, NullLogger<ScanCoordinator>.Instance);

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }

    // Four frames catalogued, three deleted from disk: 3 of 4 is past the 50 percent limit.
    private async Task<ScanCoordinator> ALibraryPastTheLimit()
    {
        WriteFrame("keep.fits");
        var doomed = new[] { WriteFrame("gone1.fits"), WriteFrame("gone2.fits"), WriteFrame("gone3.fits") };
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        foreach (var frame in doomed)
        {
            File.Delete(frame);
        }

        return coordinator;
    }

    [Fact]
    public async Task ALimitedRoot_WritesOrphanPruneLimitedAtWarning()
    {
        var coordinator = await ALibraryPastTheLimit();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(0, outcome.Removed);
        using var context = OpenRead();
        var limited = context.ActivityEvents.Single(e => e.EventType == "orphan_prune_limited");
        Assert.Equal("scan", limited.Category);
        Assert.Equal("warning", limited.Severity);

        using var payload = JsonDocument.Parse(limited.Details!);
        Assert.Equal(_root, payload.RootElement.GetProperty("root").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("missing_rows").GetInt32());
        Assert.Equal(4, payload.RootElement.GetProperty("known_rows").GetInt32());

        // And nothing was deleted from the catalogue.
        Assert.Equal(4, context.Images.Count());
    }

    [Fact]
    public async Task AForcedRemoval_WritesOrphanPruneForcedAtWarning()
    {
        var coordinator = await ALibraryPastTheLimit();

        var outcome = await coordinator.RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: true));

        Assert.Equal(3, outcome.Removed);
        using var context = OpenRead();
        var forced = context.ActivityEvents.Single(e => e.EventType == "orphan_prune_forced");
        Assert.Equal("scan", forced.Category);
        Assert.Equal("warning", forced.Severity);

        // A forced run writes the forced event and NOT the limited one.
        Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "orphan_prune_limited");
        Assert.Equal(1, context.Images.Count());
    }

    [Fact]
    public async Task OrphanPruneForced_CarriesTheRemovedTotalAndPercentage()
    {
        var coordinator = await ALibraryPastTheLimit();

        await coordinator.RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: true));

        using var context = OpenRead();
        var forced = context.ActivityEvents.Single(e => e.EventType == "orphan_prune_forced");
        using var payload = JsonDocument.Parse(forced.Details!);

        Assert.Equal(_root, payload.RootElement.GetProperty("root").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("removed").GetInt32());
        Assert.Equal(4, payload.RootElement.GetProperty("known_rows").GetInt32());

        // A number, not a formatted string, rounded to one decimal place.
        var percent = payload.RootElement.GetProperty("percent_removed");
        Assert.Equal(JsonValueKind.Number, percent.ValueKind);
        Assert.Equal(75.0, percent.GetDouble());

        Assert.True(payload.RootElement.GetProperty("forced").GetBoolean());
    }

    // HANDOFF rule 9: every details key is snake_case.
    [Theory]
    [InlineData(false, "orphan_prune_limited", new[] { "root", "missing_rows", "known_rows" })]
    [InlineData(true, "orphan_prune_forced", new[] { "root", "removed", "known_rows", "percent_removed", "forced" })]
    public async Task BothPayloads_AreSnakeCase(bool force, string eventType, string[] expectedKeys)
    {
        var coordinator = await ALibraryPastTheLimit();

        await coordinator.RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: force));

        using var context = OpenRead();
        var row = context.ActivityEvents.Single(e => e.EventType == eventType);
        using var payload = JsonDocument.Parse(row.Details!);

        var keys = payload.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(expectedKeys.Order().ToArray(), keys.Order().ToArray());
        Assert.All(keys, key => Assert.Matches("^[a-z][a-z0-9_]*$", key));
    }

    [Theory]
    [InlineData(false, "orphan_prune_limited")]
    [InlineData(true, "orphan_prune_forced")]
    public async Task BothEvents_CarryTheScanAsTheirParent(bool force, string eventType)
    {
        var coordinator = await ALibraryPastTheLimit();

        await coordinator.RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: force));

        using var context = OpenRead();
        var events = context.ActivityEvents.OrderBy(e => e.Id).ToList();
        var started = events.Last(e => e.EventType == "scan_started");
        var row = events.Single(e => e.EventType == eventType);

        Assert.Equal(started.Id, row.ParentId);
    }

    // The two events that were already there keep their own shape: the new pair is added beside
    // them, not in place of either.
    [Fact]
    public async Task TheExistingTwoEvents_AreUnchanged()
    {
        // Below the limit, so the pass removes one row and writes orphans_pruned at info.
        WriteFrame("keep1.fits");
        WriteFrame("keep2.fits");
        WriteFrame("keep3.fits");
        var doomed = WriteFrame("gone.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        File.Delete(doomed);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using (var context = OpenRead())
        {
            var pruned = context.ActivityEvents.Single(e => e.EventType == "orphans_pruned");
            Assert.Equal("info", pruned.Severity);
            using var payload = JsonDocument.Parse(pruned.Details!);
            Assert.Equal(1, payload.RootElement.GetProperty("count").GetInt32());
            Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "orphan_prune_limited");
        }

        // And the zero-discovery guard still writes orphan_prune_skipped at warning, with its own
        // two keys, even on a forced run.
        foreach (var frame in Directory.GetFiles(_root))
        {
            File.Delete(frame);
        }

        await coordinator.RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: true));

        using var after = OpenRead();
        var skipped = after.ActivityEvents.Single(e => e.EventType == "orphan_prune_skipped");
        Assert.Equal("warning", skipped.Severity);
        using var skippedPayload = JsonDocument.Parse(skipped.Details!);
        Assert.Equal(3, skippedPayload.RootElement.GetProperty("known_rows").GetInt32());
        Assert.Equal(_root, skippedPayload.RootElement.GetProperty("root").GetString());

        // The absolute guard held: the catalogue is not emptied by a forced run.
        Assert.Equal(3, after.Images.Count());
    }
}
