using GalactiLog.Core.Settings;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

public class AliasMapCacheTests
{
    private static (AliasMapCache Cache, SettingsStore Store, TestDatabaseHandle Db) CreateCache(Func<DateTime>? utcNow = null)
    {
        var db = TestDatabaseFactory.CreateMigratedDatabase();
        var store = new SettingsStore(new SettingsRepository(db.ConnectionString));
        return (new AliasMapCache(store, utcNow), store, db);
    }

    [Fact]
    public void Current_ReturnsSameInstance_WithinTtl()
    {
        var (cache, _, db) = CreateCache();
        using var _1 = db;
        using var _2 = cache;

        var first = cache.Current;
        var second = cache.Current;

        Assert.Same(first, second);
    }

    [Fact]
    public void SaveFilters_InvalidatesCache_NextReadSeesNewAlias()
    {
        var (cache, store, db) = CreateCache();
        using var _1 = db;
        using var _2 = cache;

        var before = cache.Current;
        Assert.Equal("O3", cache.Current.CanonicalFilter("O3"));

        store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new FilterSetting { Aliases = ["O3"] },
        });

        var after = cache.Current;
        Assert.NotSame(before, after);
        Assert.Equal("OIII", after.CanonicalFilter("O3"));
    }

    [Fact]
    public void SaveEquipment_InvalidatesCache_NextReadSeesNewCamera()
    {
        var (cache, store, db) = CreateCache();
        using var _1 = db;
        using var _2 = cache;

        var before = cache.Current;
        Assert.Empty(before.ConfiguredCameras);

        store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings(),
            },
        });

        var after = cache.Current;
        Assert.NotSame(before, after);
        Assert.Equal(new[] { "ASI2600MM" }, after.ConfiguredCameras);
    }

    [Fact]
    public void Current_AfterTtlElapses_RebuildsEvenWithoutInvalidation()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (cache, _, db) = CreateCache(() => now);
        using var _1 = db;
        using var _2 = cache;

        var before = cache.Current;
        now = now.AddSeconds(31);
        var after = cache.Current;

        Assert.NotSame(before, after);
    }

    [Fact]
    public void Invalidate_ForcesRebuild()
    {
        var (cache, _, db) = CreateCache();
        using var _1 = db;
        using var _2 = cache;

        var before = cache.Current;
        cache.Invalidate();
        var after = cache.Current;

        Assert.NotSame(before, after);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSettingsStore()
    {
        var (cache, store, db) = CreateCache();
        using var _1 = db;

        var before = cache.Current;
        cache.Dispose();

        store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new FilterSetting { Aliases = ["O3"] },
        });

        var after = cache.Current;
        Assert.Same(before, after);
    }
}
