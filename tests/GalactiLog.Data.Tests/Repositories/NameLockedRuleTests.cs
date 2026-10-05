using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

/// <summary>
/// Ruling D5's pin, the case spec 9.7 asks for by name: "No automatic pass ever re-resolves a
/// <c>name_locked</c> target. A scan, the retry action, duplicate detection and smart rebuild may
/// link frames to such a target by name or alias and may add an alias to it, and they write
/// nothing else."
/// </summary>
/// <remarks>
/// <para>
/// The automatic pass under test is <c>TargetResolver.Resolve</c>, which is the resolution step
/// every automatic pass shares: the scan writer calls it per record, the retry calls it per
/// unresolved name, and smart rebuild calls it per name in cache-only mode. Driving it directly
/// rather than through a whole <c>ScanCoordinator</c> run keeps the assertion on the write that
/// the rule is about, and needs no generated FITS fixture.
/// </para>
/// <para>
/// Every resolver here is built over an HTTP handler that throws, so a pass that reached the
/// network would fail loudly rather than pass quietly.
/// </para>
/// </remarks>
public sealed class NameLockedRuleTests : IDisposable
{
    private static readonly DateOnly SessionDate = new(2026, 1, 4);

    private readonly CreateTargetHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }

    private TargetResolver MakeResolver()
    {
        var handler = new ThrowingHandler();
        return new TargetResolver(
            _harness.Cs,
            StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_harness.Cs),
            new SimbadClient(handler),
            new SesameClient(handler));
    }

    /// <summary>The seven columns spec 9.7 names, as one comparable tuple.</summary>
    private static (string, string?, string?, string?, double?, double?, string?) Identity(Target target)
        => (target.PrimaryName, target.CatalogId, target.CatalogIdNormalized, target.CommonName,
            target.Ra, target.Dec, target.ObjectType);

    private void AddFrames(string objectName, int count, Guid? targetId = null)
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(_harness.Cs, targetId, SessionDate, image =>
                image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName));
        }
    }

    private int LightFramesOn(Guid targetId)
    {
        using var context = _harness.Open();
        return context.Images.Count(row => row.ResolvedTargetId == targetId && row.ImageType == "LIGHT");
    }

    /// <summary>A user-defined target created through the path under test, named after a real
    /// catalogue object, so an automatic re-resolve would have plenty to overwrite it with.</summary>
    private Guid CreateUserDefinedM31()
    {
        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request(
            "M 31", objectType: "Comet", ra: 1d, dec: 2d, catalogId: "Kumar 1"));
        Assert.Equal(CreateTargetOutcome.Created, result.Outcome);
        return result.TargetId!.Value;
    }

    [Fact]
    public void AScanOverAUserDefinedTargetsName_ChangesNoIdentityColumn()
    {
        var id = CreateUserDefinedM31();
        var before = Identity(_harness.Read(id));

        var result = MakeResolver().Resolve("M 31");

        // It linked to the user's target rather than creating a second one.
        Assert.Equal(id, result.TargetId);
        using (var context = _harness.Open())
        {
            Assert.Single(context.Targets);
        }

        Assert.Equal(before, Identity(_harness.Read(id)));
    }

    // Phase 14B fixer, fixer list item 25 (task3-review P3): renamed to what it asserts. No scan
    // runs here. The resolver is driven directly and the frames are inserted the way the scan
    // writer inserts them, against the id the resolver answered with, which is the half of the
    // path this assembly can reach; a real scan run is ScanCoordinator's and is pinned there.
    [Fact]
    public void TheResolversAnswerForAUserDefinedTargetsName_IsTheIdItsFramesLandOn()
    {
        var id = CreateUserDefinedM31();
        Assert.Equal(0, LightFramesOn(id));

        var result = MakeResolver().Resolve("M 31");
        Assert.Equal(id, result.TargetId);

        // What the scan writer does with the id the resolver returned.
        AddFrames("M 31", 3, targetId: result.TargetId);

        Assert.Equal(3, LightFramesOn(id));
        Assert.True(_harness.Read(id).NameLocked);
    }

    [Fact]
    public void RebuildTargets_LeavesAUserDefinedTargetsFramesAssigned()
    {
        var id = CreateUserDefinedM31();
        AddFrames("M 31", 3, targetId: id);
        var before = Identity(_harness.Read(id));

        var resolver = MakeResolver();
        var rebuild = new TargetRebuild(
            _harness.Cs,
            (name, ct) => resolver.Resolve(name, createIfMissing: true, dryRun: false, skipOnline: true, ct: ct),
            () => new Lease(),
            NullLogger.Instance);

        rebuild.Run((_, _, _) => { }, CancellationToken.None);

        // TargetRebuild.UnassignNonUserDefinedFrames skips user_defined targets, so the frames
        // stay where the user put them and nothing rewrote the row.
        Assert.Equal(3, LightFramesOn(id));
        Assert.Equal(before, Identity(_harness.Read(id)));
    }

    [Fact]
    public void TheRetry_DoesNotReResolveANameLockedTarget()
    {
        var id = CreateUserDefinedM31();
        AddFrames("M 31", 2);
        var before = Identity(_harness.Read(id));

        var resolver = MakeResolver();
        var outcome = new UnresolvedRetry(
            _harness.Cs,
            new CatalogCacheRepository(_harness.Cs),
            (name, ct) => resolver.Resolve(name, ct: ct),
            () => new Lease(),
            NullLogger.Instance).Run((_, _, _) => { }, CancellationToken.None);

        Assert.Equal(UnresolvedRetry.RetryStatus.Completed, outcome.Status);

        // The retry is allowed to move the frames onto the target, and nothing else.
        Assert.Equal(2, LightFramesOn(id));
        Assert.Equal(before, Identity(_harness.Read(id)));
    }

    [Fact]
    public void ANameLockedButNotUserDefinedTarget_IsAlsoProtected()
    {
        // A target the user renamed is name_locked without user_defined (TargetWriteRepository.
        // Rename), which spec 9.7's "either flag alone is enough to stop the write" covers.
        var created = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("M 31", userDefined: false));
        var id = created.TargetId!.Value;

        var target = _harness.Read(id);
        Assert.True(target.NameLocked);
        Assert.False(target.UserDefined);
        var before = Identity(target);

        var result = MakeResolver().Resolve("M 31");

        Assert.Equal(id, result.TargetId);
        Assert.Equal(before, Identity(_harness.Read(id)));
    }
}
