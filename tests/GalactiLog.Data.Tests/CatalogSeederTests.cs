using GalactiLog.Core.Catalogs;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Uses the real shipped Catalogs directory (copied next to this test assembly by the
// GalactiLog.Data.Tests.csproj ItemGroup), per spec 18.2's fixture policy: catalogs are
// shipped content, not generated fixtures, so this is the one Phase 3 task that reads real
// files in tests.
public class CatalogSeederTests
{
    private const int ExpectedOpenNgcRows = 13969;
    private const int ExpectedStaticRows = 4822;

    private static (CatalogSeeder Seeder, TestDatabaseHandle Db) CreateSeeder()
    {
        var db = TestDatabaseFactory.CreateMigratedDatabase();
        var store = new SettingsStore(new SettingsRepository(db.ConnectionString));
        return (new CatalogSeeder(db.ConnectionString, store), db);
    }

    [Fact]
    public void LoadIfNeeded_LoadsExpectedRowCounts()
    {
        var (seeder, db) = CreateSeeder();
        using var _ = db;

        var result = seeder.LoadIfNeeded(StaticCatalogLoader.ResolveCatalogsDirectory());

        Assert.False(result.Skipped);
        Assert.Equal(ExpectedOpenNgcRows, result.OpenNgcRowsLoaded);
        Assert.Equal(ExpectedStaticRows, result.StaticRowsLoaded);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        Assert.Equal(ExpectedOpenNgcRows, context.OpenNgcCatalogEntries.Count());
        Assert.Equal(ExpectedStaticRows, context.StaticCatalogEntries.Count());
        Assert.Equal(5, context.StaticCatalogEntries.Select(e => e.CatalogName).Distinct().Count());
    }

    [Fact]
    public void LoadIfNeeded_SecondCallWithVersionSetPerformsNoWrites()
    {
        var (seeder, db) = CreateSeeder();
        using var _ = db;
        var catalogsDir = StaticCatalogLoader.ResolveCatalogsDirectory();

        var first = seeder.LoadIfNeeded(catalogsDir);
        Assert.False(first.Skipped);

        var store = new SettingsStore(new SettingsRepository(db.ConnectionString));
        Assert.Equal(CatalogSeeder.CurrentCatalogsVersion, store.GetGeneral().CatalogsLoadedVersion);

        var second = seeder.LoadIfNeeded(catalogsDir);
        Assert.True(second.Skipped);
        Assert.Equal(0, second.OpenNgcRowsLoaded);
        Assert.Equal(0, second.StaticRowsLoaded);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        Assert.Equal(ExpectedOpenNgcRows, context.OpenNgcCatalogEntries.Count());
        Assert.Equal(ExpectedStaticRows, context.StaticCatalogEntries.Count());
    }

    [Fact]
    public void LoadIfNeeded_IsIdempotentEvenIfVersionFlagIsStale()
    {
        var (seeder, db) = CreateSeeder();
        using var _ = db;
        var catalogsDir = StaticCatalogLoader.ResolveCatalogsDirectory();

        seeder.LoadIfNeeded(catalogsDir);

        // Simulate a corrupted/rolled-back settings row: the version flag is stale, but the
        // tables themselves are untouched.
        var store = new SettingsStore(new SettingsRepository(db.ConnectionString));
        store.SaveGeneral(store.GetGeneral() with { CatalogsLoadedVersion = 0 });

        seeder.LoadIfNeeded(catalogsDir);

        // The per-table .Any() guard, not the version check, is what prevented a duplicate
        // load.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString));
        Assert.Equal(ExpectedOpenNgcRows, context.OpenNgcCatalogEntries.Count());
        Assert.Equal(ExpectedStaticRows, context.StaticCatalogEntries.Count());
    }
}
