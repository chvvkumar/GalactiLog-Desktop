using System.Net;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Spec 9.7's creation steps 2 and 5 applied to a target that already exists (FIXER LIST item 13).
// Against real bundled catalogs, so the OpenNGC, SAC and membership passes are the production ones
// rather than stubs; no test here touches the network.
public class TargetEnrichmentRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly string _catalogsDirectory;
    private readonly TargetEnrichmentRepository _repository;

    public TargetEnrichmentRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateSeededDatabase();
        _catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        _repository = new TargetEnrichmentRepository(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    private GalactiLogContext OpenContext() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private Guid Seed(string primaryName, Action<Target>? configure = null)
    {
        var target = new Target { Id = Guid.NewGuid(), PrimaryName = primaryName, Aliases = "[]" };
        configure?.Invoke(target);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.Targets.Add(target);
        context.SaveChanges();
        return target.Id;
    }

    private Target Read(Guid id)
    {
        using var context = OpenContext();
        return context.Targets.Single(row => row.Id == id);
    }

    private static ResolvedIdentity Identity(
        string primaryName = "M 31 (Andromeda Galaxy)",
        string? catalogId = "M 31",
        string? commonName = "Andromeda Galaxy",
        double? ra = 10.6847,
        double? dec = 41.269,
        string? objectType = "G",
        params string[] aliases)
        => new(primaryName, catalogId, commonName, ra, dec, objectType, aliases);

    [Fact]
    public void ReEnrich_WritesTheCatalogColumnsFromTheIdentity()
    {
        var id = Seed("obj:andromeda");

        var result = _repository.ReEnrich(id, Identity());

        Assert.Equal(EnrichmentOutcome.Enriched, result.Outcome);
        var target = Read(id);
        Assert.Equal("M 31 (Andromeda Galaxy)", target.PrimaryName);
        Assert.Equal("M 31", target.CatalogId);
        Assert.Equal("M 31", target.CatalogIdNormalized);
        Assert.Equal("Andromeda Galaxy", target.CommonName);
        Assert.Equal(10.6847, target.Ra);
        Assert.Equal(41.269, target.Dec);
        Assert.Equal("G", target.ObjectType);
        Assert.Contains("catalog id", result.FieldsChanged);
        Assert.Contains("object type", result.FieldsChanged);
    }

    [Fact]
    public void ReEnrich_NeverOverwritesAStoredValueWithNull()
    {
        var id = Seed("M 31", target =>
        {
            target.CatalogId = "M 31";
            target.CatalogIdNormalized = "M 31";
            target.CommonName = "Andromeda Galaxy";
            target.Ra = 10.6847;
            target.Dec = 41.269;
            target.ObjectType = "G";
        });

        // An identity that has lost every optional field must not erase what the database has.
        var result = _repository.ReEnrich(id, new ResolvedIdentity("M 31", null, null, null, null, null, []));

        // The OpenNGC pass still fills what it can (this target is a real catalogue object), so
        // the outcome is Enriched; what matters here is that no identity field was cleared.
        Assert.DoesNotContain("catalog id", result.FieldsChanged);
        Assert.DoesNotContain("common name", result.FieldsChanged);
        Assert.DoesNotContain("object type", result.FieldsChanged);
        var target = Read(id);
        Assert.Equal("M 31", target.CatalogId);
        Assert.Equal("Andromeda Galaxy", target.CommonName);
        Assert.Equal(10.6847, target.Ra);
        Assert.Equal(41.269, target.Dec);
        Assert.Equal("G", target.ObjectType);
    }

    [Fact]
    public void ReEnrich_UnionsTheAliasesCaseInsensitively()
    {
        var id = Seed("M 31", target => target.Aliases = JsonSerializer.Serialize(new[] { "M 31", "ANDROMEDA" }));

        _repository.ReEnrich(id, Identity(aliases: ["m 31", "andromeda", "NGC 224"]));

        var aliases = JsonSerializer.Deserialize<List<string>>(Read(id).Aliases)!;
        // Order preserved, nothing removed, the case-insensitive duplicates not re-added.
        Assert.Equal(["M 31", "ANDROMEDA", "NGC 224"], aliases);
    }

    [Fact]
    public void ReEnrich_RewritesThePrimaryNameWhenTheNameIsNotLocked()
    {
        var id = Seed("obj:andromeda");

        var result = _repository.ReEnrich(id, Identity());

        Assert.Equal("M 31 (Andromeda Galaxy)", Read(id).PrimaryName);
        Assert.Contains("primary name", result.FieldsChanged);
    }

    [Fact]
    public void ReEnrich_LeavesALockedPrimaryNameAlone()
    {
        // Spec 5.3: name_locked is set when the user renames a target, and a rename is the user's.
        var id = Seed("My Andromeda", target => target.NameLocked = true);

        var result = _repository.ReEnrich(id, Identity());

        Assert.Equal("My Andromeda", Read(id).PrimaryName);
        Assert.True(Read(id).NameLocked);
        Assert.DoesNotContain("primary name", result.FieldsChanged);
    }

    [Fact]
    public void ReEnrich_LockedName_StillEnrichesTheOtherFields()
    {
        // The OpenNGC pass rewrites primary_name when it fills common_name, so this case proves
        // the locked name survives that pass as well as the identity write.
        var id = Seed("My Pelican", target => target.NameLocked = true);

        var result = _repository.ReEnrich(id, new ResolvedIdentity("NGC 7000", "NGC 7000", null, 314.8, 44.5, "HII", []));

        Assert.Equal(EnrichmentOutcome.Enriched, result.Outcome);
        var target = Read(id);
        Assert.Equal("My Pelican", target.PrimaryName);
        Assert.Equal("NGC 7000", target.CatalogId);
        Assert.Equal("Cyg", target.Constellation);
        Assert.Equal("North America Nebula", target.CommonName);
    }

    [Fact]
    public void ReEnrich_RunsOpenNgcEnrichment()
    {
        var id = Seed("obj:north america");

        var result = _repository.ReEnrich(id, new ResolvedIdentity("NGC 7000", "NGC 7000", null, 314.8, 44.5, "HII", []));

        var target = Read(id);
        Assert.Equal("Cyg", target.Constellation);
        Assert.Equal(120.0, target.SizeMajor);
        Assert.Equal(30.0, target.SizeMinor);
        // Catalogs/openngc.csv carries no V-Mag for this row, and the pass is a null-only fill,
        // so the column stays null rather than borrowing the B-Mag beside it.
        Assert.Null(target.VMag);
        Assert.Contains("constellation", result.FieldsChanged);
    }

    [Fact]
    public void ReEnrich_FillsTheSacColumnsOnlyWhenNull()
    {
        var empty = Seed("obj:andromeda");
        var occupied = Seed("obj:north america", target => target.SacDescription = "my own description");

        _repository.ReEnrich(empty, Identity());
        // Review finding 2: the occupied half is seeded against a SAC-matched identity of its own
        // (NGC 7000 has a row in Catalogs/sac.csv), so the assertion below fails if the null-only
        // rule is dropped. With a null catalog_id the identifier set would be empty and the SAC
        // pass would decline to write for a reason that has nothing to do with the rule under test.
        _repository.ReEnrich(occupied, new ResolvedIdentity("NGC 7000", "NGC 7000", null, 314.8, 44.5, "HII", []));

        // Catalogs/sac.csv's M 31 row carries the description and an empty "Other" column.
        Assert.Equal("Andromeda Galaxy; nearest large spiral", Read(empty).SacDescription);
        Assert.Null(Read(empty).SacNotes);
        Assert.Equal("my own description", Read(occupied).SacDescription);
    }

    [Fact]
    public void ReEnrich_UpsertsCatalogMemberships()
    {
        var id = Seed("obj:andromeda");

        _repository.ReEnrich(id, Identity());
        // Idempotent by the unique (target_id, catalog_name) constraint, so a second pass adds
        // no row.
        _repository.ReEnrich(id, Identity());

        using var context = OpenContext();
        var memberships = context.TargetCatalogMemberships.Where(row => row.TargetId == id).ToList();
        Assert.NotEmpty(memberships);
        Assert.Contains(memberships, row => row.CatalogName == "messier" && row.CatalogNumber == "31");
        Assert.Equal(memberships.Select(row => row.CatalogName).Distinct().Count(), memberships.Count);
    }

    [Fact]
    public void ReEnrich_IsIdempotent()
    {
        var id = Seed("obj:andromeda");
        _repository.ReEnrich(id, Identity());

        var second = _repository.ReEnrich(id, Identity());

        Assert.Equal(EnrichmentOutcome.Unchanged, second.Outcome);
        Assert.Empty(second.FieldsChanged);
    }

    [Fact]
    public void ReEnrich_UserDefinedTarget_IsSuppressed()
    {
        // Spec 5.3: user_defined "suppresses catalog enrichment overwrites".
        var id = Seed("My Own Object", target =>
        {
            target.UserDefined = true;
            target.ObjectType = "Planet";
        });

        var result = _repository.ReEnrich(id, Identity());

        Assert.Equal(EnrichmentOutcome.Suppressed, result.Outcome);
        Assert.Empty(result.FieldsChanged);
        var target = Read(id);
        Assert.Equal("My Own Object", target.PrimaryName);
        Assert.Equal("Planet", target.ObjectType);
        Assert.Null(target.CatalogId);
    }

    [Fact]
    public void ReEnrich_SolarSystemTargetFromPart1_IsSuppressed()
    {
        // The real Part 1 path: TargetResolver classifies "Jupiter" and creates a user_defined
        // target, which this writer must then refuse to flatten.
        var resolver = new TargetResolver(
            _db.ConnectionString, _catalogsDirectory,
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(NoMatchHandler()), new SesameClient(NoMatchHandler()));
        var created = resolver.Resolve("Jupiter");
        Assert.Equal(TargetResolver.ResolutionStage.SolarSystem, created.Stage);

        var result = _repository.ReEnrich(created.TargetId!.Value, Identity());

        Assert.Equal(EnrichmentOutcome.Suppressed, result.Outcome);
        var target = Read(created.TargetId!.Value);
        Assert.Equal("Jupiter", target.PrimaryName);
        Assert.Equal("Planet", target.ObjectType);
        Assert.Equal("Planet", ObjectTypeCategories.Categorize(target.ObjectType));
    }

    [Fact]
    public void ReEnrich_NameCollision_ReturnsConflictAndWritesNothing()
    {
        Seed("M 31 (Andromeda Galaxy)", target =>
        {
            target.CatalogId = "M 31";
            target.CatalogIdNormalized = "M 31";
        });
        var id = Seed("obj:andromeda");

        var result = _repository.ReEnrich(id, Identity());

        // primary_name is unique among unmerged targets and catalog_id_normalized is unique
        // where merged_into_id is null (spec 5.3). SQLite rolls the failed save back.
        Assert.Equal(EnrichmentOutcome.Conflict, result.Outcome);
        Assert.Empty(result.FieldsChanged);
        var target = Read(id);
        Assert.Equal("obj:andromeda", target.PrimaryName);
        Assert.Null(target.CatalogId);
        Assert.Null(target.Constellation);
    }

    [Fact]
    public void ReEnrich_UnknownTarget_ReturnsNotFound()
    {
        var result = _repository.ReEnrich(Guid.NewGuid(), Identity());

        Assert.Equal(EnrichmentOutcome.NotFound, result.Outcome);
        Assert.Empty(result.FieldsChanged);
    }

    [Fact]
    public void ReEnrich_NeverTouchesNotesThumbnailOrMergeColumns()
    {
        // An active target, so the write actually happens and the assertions below are about what
        // the writer left alone rather than about a call that declined to run.
        var id = Seed("obj:andromeda", target =>
        {
            target.Notes = "my note";
            target.ReferenceThumbnailPath = @"thumbs\ref.jpg";
        });

        var result = _repository.ReEnrich(id, Identity());

        Assert.Equal(EnrichmentOutcome.Enriched, result.Outcome);
        var target = Read(id);
        Assert.Equal("my note", target.Notes);
        Assert.Equal(@"thumbs\ref.jpg", target.ReferenceThumbnailPath);
        Assert.Null(target.MergedIntoId);
        Assert.Null(target.MergedAt);
        Assert.False(target.NameLocked);
    }

    // Review finding 1. Spec 9.7: a merged-away row keeps its own columns and is excluded from
    // every lookup. Re-enriching one could rewrite it onto the winner's name or catalog identity,
    // which the partial unique indexes would not catch, because they only cover unmerged rows.
    [Fact]
    public void ReEnrich_MergedAwayTarget_ReturnsNotFoundAndWritesNothing()
    {
        var winner = Seed("The Winner");
        var mergedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var loser = Seed("obj:andromeda", target =>
        {
            target.MergedIntoId = winner;
            target.MergedAt = mergedAt;
        });

        var result = _repository.ReEnrich(loser, Identity());

        Assert.Equal(EnrichmentOutcome.NotFound, result.Outcome);
        Assert.Empty(result.FieldsChanged);
        var target = Read(loser);
        Assert.Equal("obj:andromeda", target.PrimaryName);
        Assert.Null(target.CatalogId);
        Assert.Null(target.Constellation);
        Assert.Equal(winner, target.MergedIntoId);
        Assert.Equal(mergedAt, target.MergedAt);
    }

    private static StubHandler NoMatchHandler() => new(request =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.Host.Contains("simbad", StringComparison.OrdinalIgnoreCase)
                    ? "::error::\nnot found\n"
                    : "<?xml version=\"1.0\"?><Sesame></Sesame>"),
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
