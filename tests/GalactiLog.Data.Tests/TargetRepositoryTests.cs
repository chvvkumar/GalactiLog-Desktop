using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

public class TargetRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly GalactiLogContext _context;
    private readonly TargetRepository _repository;

    public TargetRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        _repository = new TargetRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private Target AddTarget(string primaryName, IEnumerable<string>? aliases = null, string? catalogIdNormalized = null, Guid? mergedIntoId = null)
    {
        var target = new Target
        {
            Id = Guid.NewGuid(),
            PrimaryName = primaryName,
            CatalogIdNormalized = catalogIdNormalized,
            Aliases = JsonSerializer.Serialize(aliases?.ToList() ?? []),
            MergedIntoId = mergedIntoId,
        };
        _context.Targets.Add(target);
        _context.SaveChanges();
        return target;
    }

    [Fact]
    public void FindByAliasExact_IsCaseSensitive()
    {
        AddTarget("Rho Ophiuchi", aliases: ["rho Oph"]);

        Assert.Null(_repository.FindByAliasExact("RHO OPH"));
        Assert.NotNull(_repository.FindByAliasExact("rho Oph"));
    }

    [Fact]
    public void AddAliasIfMissing_IsCaseInsensitiveForDedup()
    {
        var target = AddTarget("NGC 7000", aliases: ["ngc 7000"]);

        _repository.AddAliasIfMissing(target, "NGC 7000");

        var aliases = JsonSerializer.Deserialize<List<string>>(target.Aliases)!;
        Assert.Single(aliases);
        Assert.Equal("ngc 7000", aliases[0]);
    }

    [Fact]
    public void FindByCatalogIdNormalized_ExcludesMergedTargets()
    {
        var winner = AddTarget("Winner", catalogIdNormalized: "NGC 1");
        AddTarget("Loser", catalogIdNormalized: "NGC 7000", mergedIntoId: winner.Id);

        Assert.Null(_repository.FindByCatalogIdNormalized("NGC 7000"));
    }

    [Fact]
    public void FindByAliasExact_ExcludesMergedTargets()
    {
        var winner = AddTarget("Winner", aliases: ["NGC 1"]);
        AddTarget("Loser", aliases: ["NGC 7000"], mergedIntoId: winner.Id);

        Assert.Null(_repository.FindByAliasExact("NGC 7000"));
    }
}
