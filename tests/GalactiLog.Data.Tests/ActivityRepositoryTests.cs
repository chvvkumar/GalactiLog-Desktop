using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Spec 5.12 (activity_events, retention) and 10.9 (the shared emit spine).
public class ActivityRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose() => _db.Dispose();

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private GalactiLogContext OpenWrite() => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    // Rows written straight to the table, so a test can place them anywhere on the timeline.
    private int SeedEvent(DateTime timestamp, int? parentId = null, string eventType = "seeded")
    {
        using var context = OpenWrite();
        var row = new ActivityEvent
        {
            Timestamp = timestamp,
            Severity = "info",
            Category = "scan",
            EventType = eventType,
            Message = eventType,
            ParentId = parentId,
        };
        context.ActivityEvents.Add(row);
        context.SaveChanges();
        return row.Id;
    }

    [Fact]
    public void Emit_WritesRowOnGivenContext_NotYetSaved()
    {
        using (var context = OpenWrite())
        {
            var evt = ActivityRepository.Emit(
                context, "scan", "warning", "file_rejected", "rejected", new { path = "x.fits" });

            // The caller owns the SaveChanges schedule: until then the row is tracked only,
            // with no identity yet, which is exactly what lets ScanWriter batch it with the
            // record that produced it.
            Assert.Equal(0, evt.Id);
            using var reader = OpenRead();
            Assert.Empty(reader.ActivityEvents);

            context.SaveChanges();
            Assert.True(evt.Id > 0);
        }

        using var after = OpenRead();
        var saved = Assert.Single(after.ActivityEvents);
        Assert.Equal("scan", saved.Category);
        Assert.Equal("warning", saved.Severity);
        Assert.Equal("file_rejected", saved.EventType);
        Assert.Contains("x.fits", saved.Details);
    }

    [Fact]
    public void Emit_NullDetails_LeavesTheColumnNull()
    {
        using var context = OpenWrite();
        ActivityRepository.Emit(context, "system", "info", "app_started", "started");
        context.SaveChanges();

        using var reader = OpenRead();
        Assert.Null(reader.ActivityEvents.Single().Details);
    }

    [Fact]
    public void EmitStandalone_SavesImmediately_ReturnsRealId()
    {
        var repository = new ActivityRepository(_db.ConnectionString);

        var id = repository.EmitStandalone(
            "user_action", "info", "settings_changed", "Settings saved",
            new { sections = new[] { "general" } }, durationMs: 12);

        Assert.True(id > 0);
        using var context = OpenRead();
        var row = context.ActivityEvents.Single();
        Assert.Equal(id, row.Id);
        Assert.Equal("user_action", row.Category);
        Assert.Equal(12, row.DurationMs);
        Assert.Contains("general", row.Details);
    }

    [Fact]
    public void EmitStandalone_ParentId_LinksToTheParentRow()
    {
        var repository = new ActivityRepository(_db.ConnectionString);
        var parent = repository.EmitStandalone("scan", "info", "scan_started", "Scan started (manual)");

        var child = repository.EmitStandalone("scan", "info", "orphans_pruned", "Removed 1 file", parentId: parent);

        using var context = OpenRead();
        Assert.Equal(parent, context.ActivityEvents.Single(e => e.Id == child).ParentId);
    }

    [Fact]
    public void PruneRetention_DeletesRowsOlderThanCutoff_KeepsNewerRows()
    {
        var now = DateTime.UtcNow;
        SeedEvent(now.AddDays(-95), eventType: "ancient");
        SeedEvent(now.AddDays(-91), eventType: "stale");
        SeedEvent(now.AddDays(-89), eventType: "recent");
        SeedEvent(now, eventType: "fresh");
        var repository = new ActivityRepository(_db.ConnectionString);

        var deleted = repository.PruneRetention(90);

        Assert.Equal(2, deleted);
        using var context = OpenRead();
        var kept = context.ActivityEvents.Select(e => e.EventType).ToList();
        Assert.Contains("recent", kept);
        Assert.Contains("fresh", kept);
        Assert.DoesNotContain("ancient", kept);
        Assert.DoesNotContain("stale", kept);
    }

    [Fact]
    public void PruneRetention_RowsDeleted_RecordsTheActivityPrunedEventItself()
    {
        SeedEvent(DateTime.UtcNow.AddDays(-120));
        var repository = new ActivityRepository(_db.ConnectionString);

        var deleted = repository.PruneRetention(90);

        Assert.Equal(1, deleted);
        using var context = OpenRead();
        var pruned = context.ActivityEvents.Single(e => e.EventType == "activity_pruned");
        Assert.Equal("system", pruned.Category);
        Assert.Equal("info", pruned.Severity);
        using var payload = JsonDocument.Parse(pruned.Details!);
        Assert.Equal(1, payload.RootElement.GetProperty("deleted_count").GetInt32());
        Assert.Equal(90, payload.RootElement.GetProperty("retention_days").GetInt32());
    }

    [Fact]
    public void PruneRetention_NothingOlderThanCutoff_WritesNoEvent()
    {
        SeedEvent(DateTime.UtcNow, eventType: "fresh");
        var repository = new ActivityRepository(_db.ConnectionString);

        var deleted = repository.PruneRetention(90);

        Assert.Equal(0, deleted);
        using var context = OpenRead();
        Assert.Equal("fresh", context.ActivityEvents.Single().EventType);
    }

    // Review item 2: a zero or negative window would put the cutoff at or after "now" and
    // wipe the entire log -- including the in-flight scan_started row every sub-event of the
    // running scan hangs off, whose terminal event would then fail its foreign key.
    // SettingsStore already rejects anything outside 1-3650; this is the floor for a caller
    // that computed the window itself.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PruneRetention_ZeroOrNegativeWindow_DeletesNothing(int retentionDays)
    {
        SeedEvent(DateTime.UtcNow.AddDays(-500), eventType: "ancient");
        SeedEvent(DateTime.UtcNow, eventType: "in_flight");
        var repository = new ActivityRepository(_db.ConnectionString);

        var deleted = repository.PruneRetention(retentionDays);

        Assert.Equal(0, deleted);
        using var context = OpenRead();
        Assert.Equal(2, context.ActivityEvents.Count());
    }

    // The parent_id FK is declared ON DELETE CASCADE (spec 5.12) and PRAGMA foreign_keys is
    // ON for every connection, so a bulk ExecuteDelete that removes an aged-out scan_started
    // row takes its sub-events with it -- which is the behaviour we want: a scan sub-event is
    // meaningless once the run it belonged to has aged out of the feed. Asserted rather than
    // assumed, because ExecuteDelete bypasses EF's own cascade handling and relies entirely
    // on the database enforcing it.
    [Fact]
    public void PruneRetention_CascadesToChildEvents()
    {
        var now = DateTime.UtcNow;
        var parent = SeedEvent(now.AddDays(-100), eventType: "scan_started");
        SeedEvent(now, parentId: parent, eventType: "orphans_pruned");
        var repository = new ActivityRepository(_db.ConnectionString);

        var deleted = repository.PruneRetention(90);

        // ExecuteDelete reports only the rows its own DELETE statement matched (the parent);
        // the child goes with it through the FK cascade.
        Assert.Equal(1, deleted);
        using var context = OpenRead();
        Assert.DoesNotContain(
            context.ActivityEvents.Select(e => e.EventType).ToList(),
            t => t is "scan_started" or "orphans_pruned");
    }
}
