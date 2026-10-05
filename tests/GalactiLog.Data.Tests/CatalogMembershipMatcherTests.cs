using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

public class CatalogMembershipMatcherTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly GalactiLogContext _context;

    public CatalogMembershipMatcherTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private Target AddTarget(string catalogId, IEnumerable<string>? aliases = null, bool userDefined = false, string? sacDescription = null, string? sacNotes = null)
    {
        var target = new Target
        {
            Id = Guid.NewGuid(),
            PrimaryName = catalogId,
            CatalogId = catalogId,
            Aliases = JsonSerializer.Serialize(aliases?.ToList() ?? []),
            UserDefined = userDefined,
            SacDescription = sacDescription,
            SacNotes = sacNotes,
        };
        _context.Targets.Add(target);
        return target;
    }

    private void AddStaticEntry(string catalogName, string catalogNumber, string ngcName, string payload)
        => _context.StaticCatalogEntries.Add(new StaticCatalogEntry
        {
            CatalogName = catalogName, CatalogNumber = catalogNumber, NgcName = ngcName, Payload = payload,
        });

    // Review fix, item 2: EnrichFromSac null-only fill, keyed off the port's ParseSac JSON
    // keys (Task 1): "notes" holds the CSV Notes column -> sac_description; "other" holds the
    // CSV Other column -> sac_notes.
    [Fact]
    public void EnrichFromSac_DoesNotOverwriteExistingSacDescription()
    {
        var target = AddTarget("NGC 900", sacDescription: "already set");
        AddStaticEntry("sac", "NGC 900", "NGC 900", "{\"notes\":\"new description\",\"other\":\"new notes\"}");
        _context.SaveChanges();

        var updated = CatalogMembershipMatcher.EnrichFromSac(_context, target);

        Assert.True(updated); // SacNotes was null and got filled, even though SacDescription did not change.
        Assert.Equal("already set", target.SacDescription);
        Assert.Equal("new notes", target.SacNotes);
    }

    [Fact]
    public void EnrichFromSac_SkipsUserDefinedTargets()
    {
        var target = AddTarget("NGC 901", userDefined: true);
        AddStaticEntry("sac", "NGC 901", "NGC 901", "{\"notes\":\"desc\",\"other\":\"notes\"}");
        _context.SaveChanges();

        var updated = CatalogMembershipMatcher.EnrichFromSac(_context, target);

        Assert.False(updated);
        Assert.Null(target.SacDescription);
        Assert.Null(target.SacNotes);
    }

    // Review fix, item 5: two static entries in the same catalog (here "arp") each matching a
    // different one of the target's identifiers (catalog_id and an alias) must collapse into
    // one membership row within MatchForTarget's single SaveChanges, not two Added rows that
    // only collide against the (target_id, catalog_name) unique constraint when SaveChanges
    // finally runs.
    [Fact]
    public void MatchForTarget_TwoStaticEntriesSameCatalogMatchingOneTarget_ProducesOneRow()
    {
        var target = AddTarget("NGC 100", aliases: ["NGC 200"]);
        AddStaticEntry("arp", "Arp 1", "NGC 100", "{}");
        AddStaticEntry("arp", "Arp 2", "NGC 200", "{}");
        _context.SaveChanges();

        CatalogMembershipMatcher.MatchForTarget(_context, target);

        using var readContext = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        var rows = readContext.TargetCatalogMemberships.Where(m => m.TargetId == target.Id && m.CatalogName == "arp").ToList();
        Assert.Single(rows);
    }
}
