using System.Text.Json;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Ruling D5's pin for Phase 14B Task 4's two passes, beside Task 3's NameLockedRuleTests: no
// automatic pass re-resolves a name-locked target, and spec 9.7 states what such a pass may still
// do to one. The three "may still" cases matter as much as the two "changes no": spec 9.7 permits
// linking frames and adding an alias explicitly, and a pass that refused those would be broken in
// the other direction.
//
// No network at all: both passes take a delegate and both delegates here are stubs.
public class NameLockedPassTests : IDisposable
{
    private readonly TestDatabaseHandle _db;

    public NameLockedPassTests() => _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose() => _db.Dispose();

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }

    private static void Ignore(int step, int total, string message) { }

    private static readonly TargetResolver.ResolutionResult Unresolved =
        new(null, TargetResolver.ResolutionStage.Unresolved, null);

    private SmartRebuild MakeSmartRebuild()
        => new(_db.ConnectionString, (_, _, _) => Unresolved, () => new Lease(), NullLogger.Instance);

    private CatalogIdentityBackfill MakeBackfill(
        Func<string, CancellationToken, TargetResolver.ResolutionResult> resolve)
        => new(_db.ConnectionString, resolve, () => new Lease(), NullLogger.Instance);

    private Target AddTarget(string name, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(_db.ConnectionString, name, configure);

    private Image AddFrame(Guid? targetId, string objectName, int day = 15)
        => LibrarySeeder.AddFrame(
            _db.ConnectionString, targetId, new DateOnly(2025, 3, day),
            image => image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName));

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private static string Aliases(params string[] aliases) => JsonSerializer.Serialize(aliases);

    private static IReadOnlyList<string> AliasesOf(Target target)
        => JsonSerializer.Deserialize<List<string>>(target.Aliases) ?? [];

    // The exact shape TargetResolver caches a positive SIMBAD answer in.
    private void AddCacheRow(string key, string mainId, params string[] aliases)
        => new CatalogCacheRepository(_db.ConnectionString).Save(
            "simbad",
            key,
            JsonSerializer.Serialize(new
            {
                MainId = mainId,
                ObjectType = "Galaxy",
                Ra = 10.68,
                Dec = 41.27,
                Aliases = aliases,
            }));

    // ---- changes no identity column ---------------------------------------------------------

    [Fact]
    public void SmartRebuild_ChangesNoIdentityColumnOfANameLockedTarget()
    {
        var locked = AddTarget("The name the user chose", t =>
        {
            t.NameLocked = true;
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
            t.CommonName = "Andromeda";
            t.Aliases = Aliases("M 31");
        });

        // Both a cache row that pass 4 would use and a catalog_id/common_name pair that pass 5
        // would rebuild the name from, so the case is not vacuous in either direction.
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = MakeSmartRebuild().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.IdentitiesReDerived);
        Assert.Equal(0, outcome.NamesRebuilt);

        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == locked.Id);
        Assert.Equal("The name the user chose", stored.PrimaryName);
        Assert.Equal("M 31", stored.CatalogId);
        Assert.Equal("Andromeda", stored.CommonName);
    }

    [Fact]
    public void SmartRebuild_ChangesNoIdentityColumnOfAUserDefinedTarget()
    {
        var userDefined = AddTarget("The name the user chose", t =>
        {
            t.UserDefined = true;
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
            t.CommonName = "Andromeda";
            t.Aliases = Aliases("M 31");
        });

        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = MakeSmartRebuild().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.IdentitiesReDerived);
        Assert.Equal(0, outcome.NamesRebuilt);

        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == userDefined.Id);
        Assert.Equal("The name the user chose", stored.PrimaryName);
        Assert.Equal("M 31", stored.CatalogId);
        Assert.Equal("Andromeda", stored.CommonName);
    }

    // ---- may still add an alias and link frames ---------------------------------------------

    [Fact]
    public void SmartRebuild_MayStillAddAnAliasToBoth()
    {
        var locked = AddTarget("Locked", t => t.NameLocked = true);
        var userDefined = AddTarget("User defined", t => t.UserDefined = true);
        AddFrame(locked.Id, "Locked object name", 10);
        AddFrame(userDefined.Id, "User object name", 11);

        var outcome = MakeSmartRebuild().Run(Ignore, CancellationToken.None);

        Assert.Equal(2, outcome.AliasesAdded);

        using var read = OpenRead();
        Assert.Contains("LOCKED OBJECT NAME", AliasesOf(read.Targets.Single(t => t.Id == locked.Id)));
        Assert.Contains("USER OBJECT NAME", AliasesOf(read.Targets.Single(t => t.Id == userDefined.Id)));
    }

    [Fact]
    public void SmartRebuild_MayStillLinkFramesToBoth()
    {
        var locked = AddTarget("Locked", t =>
        {
            t.NameLocked = true;
            t.Aliases = Aliases("LOCKED OBJECT NAME");
        });
        var userDefined = AddTarget("User defined", t =>
        {
            t.UserDefined = true;
            t.Aliases = Aliases("USER OBJECT NAME");
        });

        var lockedFrame = AddFrame(null, "Locked object name", 10);
        var userFrame = AddFrame(null, "User object name", 11);

        var outcome = MakeSmartRebuild().Run(Ignore, CancellationToken.None);

        Assert.Equal(2, outcome.FramesLinkedByAlias);

        using var read = OpenRead();
        Assert.Equal(locked.Id, read.Images.Single(i => i.Id == lockedFrame.Id).ResolvedTargetId);
        Assert.Equal(userDefined.Id, read.Images.Single(i => i.Id == userFrame.Id).ResolvedTargetId);
    }

    // ---- the backfill -----------------------------------------------------------------------

    [Fact]
    public void TheBackfill_ChangesNoIdentityColumnOfANameLockedTarget()
    {
        var locked = AddTarget("The name the user chose", t =>
        {
            t.NameLocked = true;
            t.UserDefined = true;
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
            t.CommonName = "Andromeda";
        });
        var frame = AddFrame(null, "Andromeda");

        // The cache answers with a different primary name and common name from the ones stored, so
        // a pass that re-derived anything would be visible below.
        var outcome = MakeBackfill((_, _) => new TargetResolver.ResolutionResult(
                null,
                TargetResolver.ResolutionStage.Cache,
                new ResolvedIdentity(
                    "M 31 - Andromeda Galaxy", "M 31", "Andromeda Galaxy", null, null, "Galaxy", [])))
            .Run(Ignore, CancellationToken.None);

        // The frames still move, which spec 9.7 permits, and the alias is still appended.
        Assert.Equal(1, outcome.LinkedNames);
        Assert.Equal(1, outcome.LinkedFrames);

        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == locked.Id);
        Assert.Equal("The name the user chose", stored.PrimaryName);
        Assert.Equal("M 31", stored.CatalogId);
        Assert.Equal("Andromeda", stored.CommonName);
        Assert.Contains("ANDROMEDA", AliasesOf(stored));
        Assert.Equal(locked.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }
}
