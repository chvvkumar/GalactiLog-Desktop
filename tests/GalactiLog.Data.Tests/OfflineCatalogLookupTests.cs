using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Real shipped Catalogs directory, seeded once via CatalogSeeder.LoadIfNeeded (same pattern
// as CatalogSeederTests), one GalactiLogContext reused read-only across every assertion.
public class OfflineCatalogLookupTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly GalactiLogContext _context;
    private readonly string _catalogsDirectory;

    public OfflineCatalogLookupTests()
    {
        _db = TestDatabaseFactory.CreateSeededDatabase();
        _catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        _context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Lookup_MessierDesignation_ResolvesToOpenNgcRow()
    {
        var identity = OfflineCatalogLookup.Lookup(_context, "M 31", _catalogsDirectory);

        Assert.NotNull(identity);
        Assert.Equal("M 31", identity!.CatalogId);
        Assert.NotNull(identity.Ra);
        Assert.NotNull(identity.Dec);
        var ngc224 = _context.OpenNgcCatalogEntries.Single(e => e.Name == "NGC 224");
        Assert.Equal(ngc224.Ra, identity.Ra);
        Assert.Equal(ngc224.Dec, identity.Dec);
        var expectedCommonName = OpenNgcCatalog.ExtractCommonName(ngc224.CommonNames);
        Assert.Equal(expectedCommonName, identity.CommonName);
        Assert.Contains(expectedCommonName!, identity.PrimaryName);
    }

    [Fact]
    public void Lookup_UnnormalizedNgcDesignation_ResolvesAfterNormalization()
    {
        var identity = OfflineCatalogLookup.Lookup(_context, "NGC0031", _catalogsDirectory);

        Assert.NotNull(identity);
        Assert.Equal("NGC 31", identity!.CatalogId);
    }

    [Fact]
    public void Lookup_CaldwellDesignation_FollowsIntoOpenNgc()
    {
        var identity = OfflineCatalogLookup.Lookup(_context, "Caldwell 14", _catalogsDirectory);

        Assert.NotNull(identity);
        Assert.Equal("NGC 869", identity!.CatalogId);
    }

    [Fact]
    public void Lookup_SharplessDesignation_ReturnsNullButRewriteProducesSimbadForm()
    {
        var identity = OfflineCatalogLookup.Lookup(_context, "Sh2 174", _catalogsDirectory);
        Assert.Null(identity);

        var rewritten = OfflineCatalogLookup.RewriteForOnlineQuery("Sh2 174", _catalogsDirectory);
        Assert.Equal("SH 2-174", rewritten);
    }

    [Fact]
    public void Lookup_HorseheadNebula_ReturnsNullButRewriteProducesOverrideTarget()
    {
        Assert.Equal("Barnard 33", CommonNameOverrides.Map["horsehead nebula"]);

        var identity = OfflineCatalogLookup.Lookup(_context, "Horsehead Nebula", _catalogsDirectory);
        Assert.Null(identity);

        var rewritten = OfflineCatalogLookup.RewriteForOnlineQuery("Horsehead Nebula", _catalogsDirectory);
        Assert.Equal("Barnard 33", rewritten);
    }

    [Fact]
    public void Lookup_DescriptiveSuffixStrip_ResolvesOnBasePart()
    {
        var withSuffix = OfflineCatalogLookup.Lookup(_context, "NGC 7000 - North America Nebula", _catalogsDirectory);
        var baseOnly = OfflineCatalogLookup.Lookup(_context, "NGC 7000", _catalogsDirectory);

        Assert.NotNull(withSuffix);
        Assert.NotNull(baseOnly);
        Assert.Equal(baseOnly!.CatalogId, withSuffix!.CatalogId);
        Assert.Equal(baseOnly.Ra, withSuffix.Ra);
        Assert.Equal(baseOnly.Dec, withSuffix.Dec);
    }

    [Fact]
    public void Lookup_NoMatchAnywhere_ReturnsNullWithoutThrowing()
    {
        var identity = OfflineCatalogLookup.Lookup(_context, "zzzz not a real object", _catalogsDirectory);

        Assert.Null(identity);
    }

    private static Target MakeTarget(string catalogId, string? constellation = null) => new()
    {
        Id = Guid.NewGuid(),
        PrimaryName = catalogId,
        CatalogId = catalogId,
        Constellation = constellation,
    };

    [Fact]
    public void EnrichFromOpenNgc_FillsOnlyNullFields()
    {
        var target = MakeTarget("NGC 7000", constellation: "XXX");

        var updated = OfflineCatalogLookup.EnrichFromOpenNgc(_context, target);

        var ngc7000 = _context.OpenNgcCatalogEntries.Single(e => e.Name == "NGC 7000");
        Assert.True(updated);
        Assert.Equal("XXX", target.Constellation);
        Assert.Equal(ngc7000.MajorAxis, target.SizeMajor);
        Assert.Equal(ngc7000.MinorAxis, target.SizeMinor);
        Assert.Equal(ngc7000.VMag, target.VMag);
        Assert.Equal(ngc7000.SurfaceBrightness, target.SurfaceBrightness);
    }

    [Fact]
    public void EnrichFromOpenNgc_RebuildsPrimaryNameFromOpenNgcCommonNameWhenTargetHasNone()
    {
        var target = MakeTarget("NGC 7000");
        Assert.Null(target.CommonName);

        var updated = OfflineCatalogLookup.EnrichFromOpenNgc(_context, target);

        var ngc7000 = _context.OpenNgcCatalogEntries.Single(e => e.Name == "NGC 7000");
        var expectedCommonName = OpenNgcCatalog.ExtractCommonName(ngc7000.CommonNames);
        Assert.True(updated);
        Assert.NotNull(expectedCommonName);
        Assert.Equal(expectedCommonName, target.CommonName);
        Assert.Equal(AliasCurator.BuildPrimaryName(target.CatalogId, expectedCommonName), target.PrimaryName);
    }

    [Fact]
    public void EnrichFromOpenNgc_SkipsWhenUserDefined()
    {
        var target = MakeTarget("NGC 7000");
        target.UserDefined = true;

        var updated = OfflineCatalogLookup.EnrichFromOpenNgc(_context, target);

        Assert.False(updated);
        Assert.Null(target.SizeMajor);
        Assert.Null(target.CommonName);
    }

    [Fact]
    public void EnrichFromOpenNgc_ReturnsFalseWhenCatalogIdHasNoOpenNgcRow()
    {
        var target = MakeTarget("Sh2-155");

        var updated = OfflineCatalogLookup.EnrichFromOpenNgc(_context, target);

        Assert.False(updated);
    }
}
