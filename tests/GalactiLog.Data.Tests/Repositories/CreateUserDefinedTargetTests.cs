using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

/// <summary>
/// The harness the four <c>CreateUserDefined</c> case files share (Phase 14B Task 3): one
/// migrated temp database, one repository over it, and the request builder every case shapes.
/// </summary>
/// <remarks>
/// A real database rather than a fake, for the same reason <c>TargetWriteRepositoryTests</c>
/// gives: the two partial unique indexes and the raw-SQL retro-link are the behaviour under
/// test, and neither exists in memory.
/// </remarks>
internal sealed class CreateTargetHarness : IDisposable
{
    // Seeded with the bundled OpenNGC and SAC catalogues, so "no catalog enrichment runs on this
    // path" (spec 9.7) is asserted against catalogues that genuinely carry the name being created
    // rather than against an empty table.
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateSeededDatabase();

    public CreateTargetHarness()
        => Repository = new TargetWriteRepository(new DatabaseConnectionString(_db.ConnectionString));

    public string Cs => _db.ConnectionString;

    public TargetWriteRepository Repository { get; }

    public GalactiLogContext Open() => new(GalactiLogContextOptions.Create(Cs, tracking: true));

    /// <summary>Spec 12.7's form as the repository takes it, with every field defaulted to what an
    /// untouched form carries: the checkbox on, nothing else filled in.</summary>
    public static CreateTargetRequest Request(
        string primaryName = "Comet C/2026 X1",
        string? objectType = null,
        double? ra = null,
        double? dec = null,
        string? catalogId = null,
        IReadOnlyList<string>? aliases = null,
        bool userDefined = true)
        => new(primaryName, objectType, ra, dec, catalogId, aliases ?? [], userDefined);

    public Target Read(Guid id)
    {
        using var context = Open();
        return context.Targets.Single(row => row.Id == id);
    }

    public static List<string> Aliases(Target target)
        => JsonSerializer.Deserialize<List<string>>(target.Aliases) ?? [];

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// Spec 9.7's manual creation (PAR-001), steps 1, 2, 4 and 6: what the inserted row holds and what
/// the activity event carries. The conflict check is
/// <see cref="CreateUserDefinedConflictTests"/>, the retro-link is
/// <see cref="CreateUserDefinedRetroLinkTests"/>, and ruling D5's pin is
/// <see cref="NameLockedRuleTests"/>.
/// </summary>
public sealed class CreateUserDefinedTargetTests : IDisposable
{
    private readonly CreateTargetHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private Target Create(CreateTargetRequest request)
    {
        var result = _harness.Repository.CreateUserDefined(request);
        Assert.Equal(CreateTargetOutcome.Created, result.Outcome);
        return _harness.Read(result.TargetId!.Value);
    }

    [Fact]
    public void Create_InsertsARowThatIsNameLockedAndUserDefined()
    {
        var target = Create(CreateTargetHarness.Request(userDefined: true));

        Assert.True(target.NameLocked);
        Assert.True(target.UserDefined);
    }

    [Fact]
    public void Create_WithTheCheckboxCleared_IsStillNameLocked()
    {
        // Spec 9.7: name_locked is not the checkbox. Either flag alone stops an automatic pass.
        var target = Create(CreateTargetHarness.Request(userDefined: false));

        Assert.True(target.NameLocked);
        Assert.False(target.UserDefined);
    }

    [Fact]
    public void Create_StoresTheTypedNameVerbatim()
    {
        var target = Create(CreateTargetHarness.Request("  Comet C/2026 X1  "));

        Assert.Equal("Comet C/2026 X1", target.PrimaryName);
    }

    [Fact]
    public void Create_StoresTheNormalizedPrimaryNameAsAnAlias_ButNotTheLiteralOne()
    {
        var target = Create(CreateTargetHarness.Request("Comet C/2026 X1"));

        var aliases = CreateTargetHarness.Aliases(target);
        Assert.Contains("COMET C/2026 X1", aliases);
        Assert.DoesNotContain("Comet C/2026 X1", aliases);
    }

    [Fact]
    public void Create_StoresTheTypedAliasesInTheOrderTyped()
    {
        var target = Create(CreateTargetHarness.Request(
            "Comet C/2026 X1", aliases: ["zeta", "alpha", "middle"]));

        // The typed order, then the primary name's normalized form last.
        Assert.Equal(
            ["ZETA", "ALPHA", "MIDDLE", "COMET C/2026 X1"],
            CreateTargetHarness.Aliases(target));
    }

    [Fact]
    public void Create_DeduplicatesAliasesCaseInsensitively()
    {
        var target = Create(CreateTargetHarness.Request(
            "Comet C/2026 X1", aliases: ["x1", "X1", "  x1  ", "comet c/2026 x1"]));

        Assert.Equal(["X1", "COMET C/2026 X1"], CreateTargetHarness.Aliases(target));
    }

    [Fact]
    public void Create_StoresAliasesUppercaseAndPanelStripped()
    {
        var target = Create(CreateTargetHarness.Request(
            "Andromeda Panel 2", aliases: ["m 31 panel 3", "ngc   224"]));

        Assert.Equal(["M 31", "NGC 224", "ANDROMEDA"], CreateTargetHarness.Aliases(target));
    }

    [Fact]
    public void Create_WithABlankCatalogId_StoresNullInBothCatalogColumns()
    {
        var target = Create(CreateTargetHarness.Request(catalogId: "   "));

        Assert.Null(target.CatalogId);
        Assert.Null(target.CatalogIdNormalized);
    }

    [Fact]
    public void Create_WithACatalogId_StoresItsNormalizedForm()
    {
        var target = Create(CreateTargetHarness.Request(catalogId: " ngc   224 "));

        Assert.Equal("ngc   224", target.CatalogId);
        Assert.Equal("NGC 224", target.CatalogIdNormalized);
    }

    [Fact]
    public void Create_StoresTheChosenCategoryLiteral()
    {
        var target = Create(CreateTargetHarness.Request(objectType: "Comet"));

        Assert.Equal("Comet", target.ObjectType);

        // Spec 9.8's pass-through, which is why a category literal needs no new mapping.
        Assert.Equal("Comet", ObjectTypeCategories.Categorize(target.ObjectType));
    }

    [Fact]
    public void Create_StoresTheFreeTextTypedUnderOther()
    {
        var target = Create(CreateTargetHarness.Request(objectType: "Sunspot group"));

        Assert.Equal("Sunspot group", target.ObjectType);

        // Spec 9.8: anything mapped by no code is Other, so free text needs no new mapping.
        Assert.Equal("Other", ObjectTypeCategories.Categorize(target.ObjectType));
    }

    [Fact]
    public void Create_WithNoTypeChosen_StoresNull()
    {
        Assert.Null(Create(CreateTargetHarness.Request(objectType: null)).ObjectType);
        Assert.Null(Create(CreateTargetHarness.Request("Second object", objectType: "  ")).ObjectType);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0d, -90d)]
    [InlineData(360d, 90d)]
    [InlineData(10.6847d, 41.269d)]
    public void Create_StoresRaAndDecAsTypedOrNull(double? ra, double? dec)
    {
        var target = Create(CreateTargetHarness.Request(ra: ra, dec: dec));

        Assert.Equal(ra, target.Ra);
        Assert.Equal(dec, target.Dec);
    }

    [Fact]
    public void Create_EmitsTargetCreatedWithSourceManual()
    {
        var target = Create(CreateTargetHarness.Request("Comet C/2026 X1", catalogId: "X1"));

        var evt = Assert.Single(ReadEvents());
        Assert.Equal(TargetWriteRepository.TargetCreatedEventType, evt.EventType);
        Assert.Equal("enrichment", evt.Category);
        Assert.Equal(target.Id, evt.TargetId);

        var details = Details(evt);
        Assert.Equal("manual", details.GetProperty("source").GetString());
        Assert.Equal("Comet C/2026 X1", details.GetProperty("primary_name").GetString());
        Assert.Equal("X1", details.GetProperty("catalog_id").GetString());
    }

    [Fact]
    public void Create_TheEventDetails_AreSnakeCase()
    {
        Create(CreateTargetHarness.Request());

        var names = Details(Assert.Single(ReadEvents()))
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        // HANDOFF rule 9. The census is exact, so a camelCase key or a new one fails here.
        Assert.Equal(
            ["primary_name", "catalog_id", "source", "linked_frames", "closed_candidates"],
            names);
    }

    [Fact]
    public void Create_TheEventCarriesTheLinkedFrameAndCandidateCounts()
    {
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        for (var i = 0; i < 3; i++)
        {
            LibrarySeeder.AddFrame(_harness.Cs, null, new DateOnly(2026, 1, 4), image =>
                image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Comet C/2026 X1"));
        }

        Create(CreateTargetHarness.Request("Comet C/2026 X1"));

        var details = Details(Assert.Single(ReadEvents()));
        Assert.Equal(3, details.GetProperty("linked_frames").GetInt32());
        Assert.Equal(1, details.GetProperty("closed_candidates").GetInt32());
    }

    [Fact]
    public void Create_RunsNoCatalogEnrichment()
    {
        // A name the bundled catalogues carry in full, so every column below would be filled if
        // any enrichment pass ran. Spec 9.7: "No catalog enrichment runs on this path".
        var target = Create(CreateTargetHarness.Request("M 31", catalogId: "M 31"));

        Assert.Null(target.CommonName);
        Assert.Null(target.Constellation);
        Assert.Null(target.SizeMajor);
        Assert.Null(target.SizeMinor);
        Assert.Null(target.PositionAngle);
        Assert.Null(target.VMag);
        Assert.Null(target.SurfaceBrightness);

        // The typed values survive untouched, which is the other half of the same rule.
        Assert.Equal("M 31", target.PrimaryName);
        Assert.Null(target.Ra);
        Assert.Null(target.Dec);
    }

    [Fact]
    public void Create_AddsNoCatalogMembershipRow()
    {
        Create(CreateTargetHarness.Request("M 31", catalogId: "M 31"));

        using var context = _harness.Open();
        Assert.Empty(context.TargetCatalogMemberships);
    }

    [Fact]
    public void Create_FillsNoSacDescriptionOrNotes()
    {
        var target = Create(CreateTargetHarness.Request("NGC 7331", catalogId: "NGC 7331"));

        Assert.Null(target.SacDescription);
        Assert.Null(target.SacNotes);
    }

    [Fact]
    public void Create_NeitherTheRepositoryNorItsRequest_ReachesAResolverOrTheNetwork()
    {
        // Spec 9.7's "no network call is made", proved by construction rather than by a stub:
        // the type takes no resolver delegate and names no HTTP client, so there is nothing on
        // this path that could reach one (brief section 5.5).
        var members = typeof(TargetWriteRepository)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        Assert.DoesNotContain(members, name => name.Contains("Resolver", StringComparison.Ordinal));
        Assert.DoesNotContain(members, name => name.Contains("Http", StringComparison.Ordinal));
        Assert.DoesNotContain(members, name => name.Contains("Client", StringComparison.Ordinal));
    }

    private List<ActivityEvent> ReadEvents()
    {
        using var context = _harness.Open();
        return [.. context.ActivityEvents.AsNoTracking()];
    }

    private static JsonElement Details(ActivityEvent evt)
        => JsonDocument.Parse(evt.Details!).RootElement;
}
