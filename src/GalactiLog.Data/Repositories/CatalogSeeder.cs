using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Io;
using GalactiLog.Data.Entities;

namespace GalactiLog.Data.Repositories;

// The version-guarded, idempotent DB write that StaticCatalogLoader cannot do itself
// (GalactiLog.Core has no EF Core reference, per design-spec 4.1). Lives in Data, not Core,
// because it needs GalactiLogContext -- pure parsing stays in Core, orchestration lives
// wherever the resource it needs (SettingsStore, the DbContext) actually lives.
public sealed class CatalogSeeder
{
    public const int CurrentCatalogsVersion = 1;

    private readonly string _connectionString;
    private readonly SettingsStore _settingsStore;

    public CatalogSeeder(string connectionString, SettingsStore settingsStore)
    {
        _connectionString = connectionString;
        _settingsStore = settingsStore;
    }

    public readonly record struct CatalogSeedResult(bool Skipped, int OpenNgcRowsLoaded, int StaticRowsLoaded);

    public CatalogSeedResult LoadIfNeeded(string catalogsDirectory)
    {
        var general = _settingsStore.GetGeneral();
        if (general.CatalogsLoadedVersion >= CurrentCatalogsVersion)
        {
            return new CatalogSeedResult(true, 0, 0);
        }

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString, tracking: true));

        // The .Any() guard on each table (not just the version check) is what makes this
        // literally idempotent even if general.catalogs_loaded_version is somehow wrong or
        // reset: calling LoadIfNeeded twice never doubles the row count.
        var openNgcCount = 0;
        if (!context.OpenNgcCatalogEntries.Any())
        {
            using var stream = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "openngc.csv"));
            var rows = StaticCatalogLoader.ParseOpenNgc(stream);
            context.OpenNgcCatalogEntries.AddRange(rows.Select(ToEntity));
            openNgcCount = rows.Count;
        }

        var staticCount = 0;
        if (!context.StaticCatalogEntries.Any())
        {
            var all = new List<StaticCatalogLoader.StaticCatalogRow>();
            using (var s = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "caldwell.csv"))) all.AddRange(StaticCatalogLoader.ParseCaldwell(s));
            using (var s = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "abell.csv"))) all.AddRange(StaticCatalogLoader.ParseAbell(s));
            using (var s = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "arp.csv"))) all.AddRange(StaticCatalogLoader.ParseArp(s));
            using (var s = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "sac.csv"))) all.AddRange(StaticCatalogLoader.ParseSac(s));
            using (var s = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "herschel400.csv"))) all.AddRange(StaticCatalogLoader.ParseHerschel400(s));
            context.StaticCatalogEntries.AddRange(all.Select(ToEntity));
            staticCount = all.Count;
        }

        context.SaveChanges();

        // Version write only happens after both SaveChanges() calls succeed, so a crash
        // mid-load leaves the version at its old value and a retry re-runs (each table's
        // own .Any() guard then skips whichever table already has rows).
        //
        // FIXER LIST F27: through MutateGeneral, not a GetGeneral-then-SaveGeneral pair over the
        // document read at the top of this method. The read, the mutation and the write happen
        // inside the store's one critical section, so this cannot write back a stale copy of any
        // other key. This runs single-threaded at first start today, so the change is hygiene
        // rather than a live race: what it buys is that MutateGeneral is now the only door into
        // the general document in the whole solution, which is a structural guarantee rather than
        // a convention every future writer has to remember (design-lessons rule 2).
        _settingsStore.MutateGeneral(current => current with
        {
            CatalogsLoadedVersion = CurrentCatalogsVersion,
        });
        return new CatalogSeedResult(false, openNgcCount, staticCount);
    }

    private static OpenNgcCatalogEntry ToEntity(StaticCatalogLoader.OpenNgcRow r) => new()
    {
        Name = r.Name,
        Type = r.Type,
        Ra = r.Ra,
        Dec = r.Dec,
        Constellation = r.Constellation,
        MajorAxis = r.MajorAxis,
        MinorAxis = r.MinorAxis,
        PositionAngle = r.PositionAngle,
        BMag = r.BMag,
        VMag = r.VMag,
        SurfaceBrightness = r.SurfaceBrightness,
        CommonNames = r.CommonNames,
        Messier = r.Messier,
    };

    private static StaticCatalogEntry ToEntity(StaticCatalogLoader.StaticCatalogRow r) => new()
    {
        CatalogName = r.CatalogName,
        CatalogNumber = r.CatalogNumber,
        NgcName = r.NgcName,
        Payload = r.Payload,
    };
}
