using System.Net;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests;

// The eight-step pipeline (design-spec 9.2), exercised against real bundled catalogs (offline
// path) and a stub HttpMessageHandler (online path) -- no real network in any test. The
// default handler throws if invoked at all, so any test that expects offline-only resolution
// fails loudly if it accidentally reaches the network layer.
public class TargetResolverTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly string _catalogsDirectory;
    private readonly CatalogCacheRepository _cache;

    public TargetResolverTests()
    {
        _db = TestDatabaseFactory.CreateSeededDatabase();
        _catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        _cache = new CatalogCacheRepository(_db.ConnectionString, wait: (_, _) => { });
    }

    public void Dispose() => _db.Dispose();

    private TargetResolver MakeResolver(HttpMessageHandler? handler = null)
    {
        handler ??= ThrowingHandler();
        return new TargetResolver(_db.ConnectionString, _catalogsDirectory, _cache,
            new SimbadClient(handler), new SesameClient(handler));
    }

    private static StubHttpMessageHandler ThrowingHandler()
        => new(request => throw new InvalidOperationException($"network access attempted: {request.RequestUri}"));

    private GalactiLogContext OpenContext() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private static HttpResponseMessage TextResponse(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    // Minimal stub HttpMessageHandler, local to this project (GalactiLog.Data.Tests has no
    // reference to GalactiLog.Core.Tests, where Task 5's FakeHttpMessageHandler lives).
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return respond(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void Resolve_OfflineResolvableName_NeverCallsHttpHandler()
    {
        var handler = ThrowingHandler();
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("M 31");

        Assert.Equal(TargetResolver.ResolutionStage.Offline, result.Stage);
        Assert.NotNull(result.TargetId);
        Assert.Equal(0, handler.CallCount);
        using var context = OpenContext();
        Assert.Single(context.Targets);
    }

    // Task 6c (spec 9.3): the panel token is stripped before every lookup, so a panel name
    // resolves offline to its base on a fresh catalogue, with no target to match against.
    [Theory]
    [InlineData("NGC 7000 Panel 1", "NGC 7000")]
    [InlineData("IC 1396 P1", "IC 1396")]
    public void Resolve_PanelName_OnAFreshCatalogue_ResolvesOfflineToItsBase(string objectName, string catalogId)
    {
        var handler = ThrowingHandler();

        var result = MakeResolver(handler).Resolve(objectName);

        Assert.Equal(TargetResolver.ResolutionStage.Offline, result.Stage);
        Assert.NotNull(result.TargetId);
        Assert.Equal(0, handler.CallCount);
        using var context = OpenContext();
        Assert.Equal(catalogId, Assert.Single(context.Targets).CatalogId);
    }

    [Fact]
    public void Resolve_SameNameTwice_CreatesOnlyOneTarget()
    {
        var resolver = MakeResolver();

        var first = resolver.Resolve("NGC 7000");
        var second = resolver.Resolve("NGC 7000");

        Assert.Equal(first.TargetId, second.TargetId);
        Assert.Equal(TargetResolver.ResolutionStage.Cache, second.Stage);
        Assert.NotNull(second.Identity);
        using var context = OpenContext();
        Assert.Single(context.Targets);
    }

    [Fact]
    public void Resolve_NameResolvingToExistingTargetsIdentity_LinksInsteadOfInserting()
    {
        var offlineResolver = MakeResolver();
        var first = offlineResolver.Resolve("M 31");
        Assert.Equal(TargetResolver.ResolutionStage.Offline, first.Stage);

        // A name with no offline hit whose SIMBAD alias/main-id set makes "M 31" the winning
        // CatalogPriority candidate -- ExtractCatalogId picks it straight from the main_id.
        var handler = new StubHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            if (host == "simbad.cds.unistra.fr" && path.Contains("sim-script", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "::data::\nM 31|G,LIN|10.6847|41.269\n");
            }
            if (host == "simbad.cds.unistra.fr" && path.Contains("sim-tap", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "id\n");
            }
            throw new InvalidOperationException($"unexpected request to {request.RequestUri}");
        });
        var onlineResolver = MakeResolver(handler);

        var second = onlineResolver.Resolve("Zzyzx Nonexistent Blob 42");

        Assert.Equal(first.TargetId, second.TargetId);
        using var context = OpenContext();
        Assert.Single(context.Targets);
    }

    // Spec 9.7: the web application's IntegrityError retry is deliberately absent here, so a
    // unique violation must surface rather than be swallowed.
    //
    // Review fix, item 4: the collision is an ACTIVE target already holding the primary_name
    // "NGC 7000" resolves to, with a null catalog_id_normalized so nothing earlier in the
    // pipeline links to it -- steps 2, 7 and 9.7's step 3 all look up by alias, by
    // catalog_id_normalized, or by the incoming name "NGC 7000", none of which this row
    // carries, so the insert is genuinely reached. An earlier version of this fixture used a
    // MERGED-AWAY row's catalog_id_normalized, which is no longer a violation at all (the
    // unique indexes are partial on merged_into_id IS NULL) and enshrined the very landmine
    // item 13 removed.
    [Fact]
    public void Resolve_UniqueViolationOnInsert_Propagates()
    {
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Targets.Add(new Target
            {
                Id = Guid.NewGuid(), PrimaryName = "NGC 7000 - North America Nebula",
                CatalogIdNormalized = null, Aliases = "[]",
            });
            seed.SaveChanges();
        }

        var resolver = MakeResolver();

        Assert.Throws<DbUpdateException>(() => resolver.Resolve("NGC 7000"));
    }

    // Review ruling, item 13: both unique indexes on `targets` are partial on
    // merged_into_id IS NULL (migration 0002), so a merged-away target keeping its own
    // primary_name and catalog_id_normalized never blocks re-creating an active target with
    // the same designation. Phase 7's merge can then absorb a loser's names without the
    // resolver needing to know that merges exist. This one row collides on BOTH indexes.
    [Fact]
    public void Resolve_MergedAwayTargetWithSameIdentity_DoesNotBlockCreatingActiveTarget()
    {
        var winnerId = Guid.NewGuid();
        var mergedId = Guid.NewGuid();
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Targets.Add(new Target
            {
                Id = winnerId, PrimaryName = "Winner Target", CatalogId = "NGC 1",
                CatalogIdNormalized = "NGC 1", Aliases = "[]",
            });
            seed.Targets.Add(new Target
            {
                Id = mergedId, PrimaryName = "NGC 7000 - North America Nebula",
                CatalogId = "NGC 7000", CatalogIdNormalized = "NGC 7000", Aliases = "[]",
                MergedIntoId = winnerId,
            });
            seed.SaveChanges();
        }

        var resolver = MakeResolver();

        var result = resolver.Resolve("NGC 7000");

        Assert.NotNull(result.TargetId);
        Assert.NotEqual(mergedId, result.TargetId!.Value);
        using var context = OpenContext();
        var created = context.Targets.Single(t => t.Id == result.TargetId.Value);
        Assert.Equal("NGC 7000", created.CatalogIdNormalized);
        Assert.Null(created.MergedIntoId);
    }

    [Fact]
    public void Resolve_UnresolvableName_WritesResolverNegativeCache_AndSecondCallSkipsNetwork()
    {
        // A clean "queried, found nothing" response from both sources (SIMBAD's "::error::"
        // body, SESAME's XML with no matching Resolver element) -- an HTTP 4xx status instead
        // would be a genuine client error (NonTransientCatalogException) per design-spec 9.6,
        // not the "no match" case this test exercises.
        var handler = new StubHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.Host;
            if (host == "simbad.cds.unistra.fr")
            {
                return TextResponse(HttpStatusCode.OK, "::error::\nNo object found\n");
            }
            return TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>");
        });
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("zzzz not real");

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.Null(result.TargetId);
        var cached = _cache.Get("resolver", "ZZZZ NOT REAL");
        Assert.Equal(CatalogCacheRepository.CacheHit.Negative, cached.Kind);

        var throwingHandler = ThrowingHandler();
        var secondResolver = MakeResolver(throwingHandler);

        var second = secondResolver.Resolve("zzzz not real");

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, second.Stage);
        Assert.Equal(0, throwingHandler.CallCount);
    }

    [Fact]
    public void Resolve_SimbadNetworkFailureExhaustsRetries_FallsThroughToSesameThenUnresolved()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.Host;
            if (host == "simbad.cds.unistra.fr")
            {
                throw new HttpRequestException("simulated network failure");
            }
            // SESAME queried cleanly and found nothing -- HTTP 200, no matching Resolver.
            return TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>");
        });
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("also not real");

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.Null(result.TargetId);
        // SIMBAD's exhausted-retries failure is caught per source and treated as "produced
        // nothing this time" (coordinator ruling Q7) -- it must never itself write a cache
        // row (positive or negative) for "simbad".
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("simbad", "ALSO NOT REAL").Kind);
        // Review fix, item 1: since SIMBAD Failed (rather than cleanly NoMatch), the pipeline
        // never checked this name end to end, so no "resolver" negative is written even though
        // SESAME itself came back a clean not-found -- a fresh Miss, so a later retry (once the
        // network is back) gets a real query rather than a cache-suppressed skip.
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "ALSO NOT REAL").Kind);
    }

    [Fact]
    public void Resolve_MembershipAttachedAfterCreate()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("Caldwell 14");

        Assert.NotNull(result.TargetId);
        using var context = OpenContext();
        var membership = context.TargetCatalogMemberships.Single(m => m.TargetId == result.TargetId!.Value && m.CatalogName == "caldwell");
        // Review fix, item 11: Single already throws when there is no row, so the number the
        // membership actually carries is the only thing left worth asserting.
        Assert.Equal("C14", membership.CatalogNumber);
    }

    [Fact]
    public void Resolve_MessierMembershipAttachedWhenCatalogIdIsMessierForm()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("M 31");

        Assert.NotNull(result.TargetId);
        using var context = OpenContext();
        var membership = context.TargetCatalogMemberships.Single(m => m.TargetId == result.TargetId!.Value && m.CatalogName == "messier");
        // Review fix, item 11: the bare number, unpadded, from openngc_catalog.messier's
        // stored "M 031" (NameNormalizer.MessierNumber).
        Assert.Equal("31", membership.CatalogNumber);
    }

    [Fact]
    public void Resolve_CreateIfMissingFalse_ReportsIdentityWithoutInsertingRow()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("M 31", createIfMissing: false);

        Assert.Null(result.TargetId);
        Assert.Equal(TargetResolver.ResolutionStage.Offline, result.Stage);
        Assert.NotNull(result.Identity);
        Assert.Equal("M 31", result.Identity!.CatalogId);
        Assert.Equal(AliasCurator.BuildPrimaryName(result.Identity.CatalogId, result.Identity.CommonName), result.Identity.PrimaryName);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
    }

    [Fact]
    public void Resolve_CreateIfMissingFalse_StillLinksToExistingTarget()
    {
        var resolver = MakeResolver();
        var created = resolver.Resolve("M 31");

        var result = resolver.Resolve("M 31", createIfMissing: false);

        Assert.Equal(created.TargetId, result.TargetId);
    }

    [Fact]
    public void Resolve_UserDefinedTargetNeverAutoCreatedButLookupStillMatchesByName()
    {
        var userDefinedId = Guid.NewGuid();
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Targets.Add(new Target
            {
                Id = userDefinedId, PrimaryName = "My Custom Target", Aliases = "[]", UserDefined = true,
            });
            seed.SaveChanges();
        }

        var resolver = MakeResolver();

        var result = resolver.Resolve("My Custom Target");

        Assert.Equal(userDefinedId, result.TargetId);
        Assert.Equal(TargetResolver.ResolutionStage.Cache, result.Stage);
    }

    // Review fix, item 1: both sources failing transiently must never be mistaken for "both
    // sources cleanly found nothing" -- the resolver negative cache must stay a Miss so a
    // later retry (once the network is back) gets a real query, not a cache-suppressed skip.
    [Fact]
    public void Resolve_SimbadAndSesameBothFailTransiently_ResolverCacheStaysMissNotNegative()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("still not real");

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.Null(result.TargetId);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "STILL NOT REAL").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("simbad", "STILL NOT REAL").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("sesame", "STILL NOT REAL").Kind);
    }

    // Review fix, item 3: CatalogPriority ranks Messier above NGC, so a row reachable by
    // either designation must converge on the same catalog_id_normalized and therefore the
    // same target, regardless of which form the FITS OBJECT string happened to use.
    [Fact]
    public void Resolve_MessierThenNgcDesignation_ConvergeOnOneTarget()
    {
        var resolver = MakeResolver();

        var first = resolver.Resolve("M 31");
        var second = resolver.Resolve("NGC 224");

        Assert.Equal(first.TargetId, second.TargetId);
        using var context = OpenContext();
        Assert.Single(context.Targets);
    }

    // Review fix, item 4: dry-run resolution must not write to `targets` at all, even on a
    // path that links to an already-existing target (which normally appends an alias and
    // calls SaveChanges).
    [Fact]
    public void Resolve_DryRun_IdentityMatchDoesNotMutateExistingTarget()
    {
        var offlineResolver = MakeResolver();
        var first = offlineResolver.Resolve("M 31");

        string aliasesBefore;
        using (var context = OpenContext())
        {
            aliasesBefore = context.Targets.Single(t => t.Id == first.TargetId!.Value).Aliases;
        }

        // Same SIMBAD stub as the "links instead of inserting" test: a name with no offline
        // hit whose main_id makes "M 31" the winning CatalogPriority candidate.
        var handler = new StubHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            if (host == "simbad.cds.unistra.fr" && path.Contains("sim-script", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "::data::\nM 31|G,LIN|10.6847|41.269\n");
            }
            if (host == "simbad.cds.unistra.fr" && path.Contains("sim-tap", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "id\n");
            }
            throw new InvalidOperationException($"unexpected request to {request.RequestUri}");
        });
        var onlineResolver = MakeResolver(handler);

        var result = onlineResolver.Resolve("Zzyzx Nonexistent Blob 42", dryRun: true);

        Assert.Equal(first.TargetId, result.TargetId);
        using (var context = OpenContext())
        {
            Assert.Single(context.Targets);
            var target = context.Targets.Single(t => t.Id == first.TargetId!.Value);
            Assert.Equal(aliasesBefore, target.Aliases);
        }
    }

    // Review fix, item 2: EnrichFromSac end to end through the real pipeline. Catalogs/sac.csv
    // row "NGC 869,C14,OPNCL,Per,...,h Persei; west half of Double Cluster" has both a CSV
    // Notes and a CSV Other value, and "NGC 869" is offline-resolvable directly.
    [Fact]
    public void Resolve_SacCoveredOfflineName_FillsSacDescriptionFromNotesAndSacNotesFromOther()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("NGC 869");

        Assert.NotNull(result.TargetId);
        using var context = OpenContext();
        var target = context.Targets.Single(t => t.Id == result.TargetId!.Value);
        Assert.Equal("h Persei; west half of Double Cluster", target.SacDescription);
        Assert.Equal("C14", target.SacNotes);
    }

    // Review fix, whole-phase item 1: an offline hit reached by its Messier designation
    // rewrites catalog_id to the Messier form, so unless the OpenNGC row's own NGC/IC name
    // survives into the aliases, the target carries no NGC designation at all -- and SAC
    // enrichment and every static-catalog membership are keyed by exactly that designation.
    //
    // M 82 (OpenNGC row NGC 3034) is the discriminating case: it is in both arp.csv (Arp 337)
    // and herschel400.csv, and both rows are keyed by "NGC 3034" alone. Resolving the Messier
    // form used to attach neither. M 57 does NOT discriminate -- sac.csv happens to carry both
    // an "M 57" row and an "NGC 6720" row -- so the SAC half of this is asserted through the
    // alias itself rather than through a field that a Messier-keyed row would fill anyway.
    [Fact]
    public void Resolve_MessierForm_CarriesNgcDesignationIntoAliasesAndCatalogMemberships()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("M 82");

        Assert.NotNull(result.TargetId);
        Assert.Contains("NGC 3034", result.Identity!.Aliases);
        using var context = OpenContext();
        var target = context.Targets.Single(t => t.Id == result.TargetId!.Value);
        Assert.Equal("M 82", target.CatalogId);
        Assert.Contains("NGC 3034", target.Aliases);

        var memberships = context.TargetCatalogMemberships
            .Where(m => m.TargetId == result.TargetId.Value)
            .ToDictionary(m => m.CatalogName, m => m.CatalogNumber);
        Assert.Equal("82", memberships["messier"]);
        Assert.Equal("Arp 337", memberships["arp"]);
        Assert.Equal("NGC 3034", memberships["herschel400"]);
    }

    // The other half of item 1, through SAC: "M 57" and "NGC 6720" reach the same OpenNGC row
    // and must both come out with a filled sac_description.
    [Fact]
    public void Resolve_MessierFormOfSacCoveredName_FillsSacDescription()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("M 57");

        Assert.NotNull(result.TargetId);
        using var context = OpenContext();
        var target = context.Targets.Single(t => t.Id == result.TargetId!.Value);
        Assert.Equal("M 57", target.CatalogId);
        Assert.Contains("NGC 6720", target.Aliases);
        Assert.NotNull(target.SacDescription);
    }

    // Review fix, whole-phase item 3: an empty or whitespace-only OBJECT string has nothing to
    // look up, and must never reach the network (SESAME would issue a GET with no name).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Resolve_EmptyOrWhitespaceName_IsUnresolvedWithoutTouchingTheNetwork(string objectName)
    {
        var handler = ThrowingHandler();
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve(objectName);

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.Null(result.TargetId);
        Assert.Null(result.Identity);
        Assert.Equal(0, handler.CallCount);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
        // Not negative-cached either: "" is not a name that could ever resolve, so there is
        // nothing worth remembering about it for seven days.
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "").Kind);
    }

    // Review ruling, item 14: a cancellation that lands during the retry backoff must end the
    // resolution promptly with OperationCanceledException and leave no cache row behind. The
    // handler cancels on its first call and then fails transiently, so the resolver enters the
    // 1 second backoff with an already-cancelled token.
    [Fact]
    public void Resolve_TokenCancelledDuringBackoff_ThrowsPromptlyAndWritesNoCacheRow()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new HttpRequestException("simulated network failure");
        });
        var resolver = MakeResolver(handler);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => resolver.Resolve("cancel me now", ct: cts.Token));
        elapsed.Stop();

        // "Promptly": well under the 1 second the first backoff would otherwise sleep, and
        // nowhere near the 3 seconds all three attempts of both sources would take.
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(900), $"took {elapsed.Elapsed}");
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("simbad", "CANCEL ME NOW").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("sesame", "CANCEL ME NOW").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "CANCEL ME NOW").Kind);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
    }

    // ---- skipOnline (the scan-run circuit breaker) and the result flags ----------------

    [Fact]
    public void Resolve_SkipOnlineTrue_NeverCallsSimbadOrSesame()
    {
        var handler = ThrowingHandler();
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("zzzz not real", skipOnline: true);

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.Null(result.TargetId);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Resolve_SkipOnlineTrue_UnresolvedOffline_WritesNoNegativeCache()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("zzzz not real", skipOnline: true);

        // "Never checked" must stay distinguishable from "checked and genuinely absent":
        // a negative row here would suppress a real query for 7 days once the network is
        // back, for a name this run never actually looked up.
        Assert.False(result.TransientNetworkFailure);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "ZZZZ NOT REAL").Kind);
    }

    [Fact]
    public void Resolve_SkipOnlineTrue_OfflineResolvableName_StillResolves()
    {
        var handler = ThrowingHandler();
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("M 31", skipOnline: true);

        // The breaker disables the online sources only; the offline catalog and the local
        // cache keep working, so a circuit-broken scan still resolves everything it can.
        Assert.Equal(TargetResolver.ResolutionStage.Offline, result.Stage);
        Assert.NotNull(result.TargetId);
        Assert.True(result.Created);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Resolve_TransientNetworkFailure_ReportsTransientNetworkFailureTrue()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("also not real");

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.True(result.TransientNetworkFailure);
        Assert.False(result.Created);
    }

    [Fact]
    public void Resolve_CleanNoMatch_ReportsTransientNetworkFailureFalse()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host == "simbad.cds.unistra.fr"
            ? TextResponse(HttpStatusCode.OK, "::error::\nNo object found\n")
            : TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>"));
        var resolver = MakeResolver(handler);

        var result = resolver.Resolve("zzzz not real");

        // Queried and genuinely absent: safe to negative-cache, and no reason to trip the
        // scan's circuit breaker.
        Assert.False(result.TransientNetworkFailure);
        Assert.Equal(CatalogCacheRepository.CacheHit.Negative, _cache.Get("resolver", "ZZZZ NOT REAL").Kind);
    }

    [Fact]
    public void Resolve_NewTargetInserted_ReportsCreatedTrue()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("M 31");

        Assert.True(result.Created);
        using var context = OpenContext();
        Assert.Single(context.Targets);
    }

    [Fact]
    public void Resolve_LinkedToExistingTarget_ReportsCreatedFalse()
    {
        var resolver = MakeResolver();
        var first = resolver.Resolve("M 31");
        Assert.True(first.Created);

        // Step 2 (existing target by name) and step 3 (offline hit linking to the target
        // that already carries this identity) must both report Created: false, so the scan
        // writer emits exactly one target_created event per real insert.
        var second = resolver.Resolve("M 31");
        var third = resolver.Resolve("NGC 224");

        Assert.False(second.Created);
        Assert.False(third.Created);
        Assert.Equal(first.TargetId, second.TargetId);
        Assert.Equal(first.TargetId, third.TargetId);
        using var context = OpenContext();
        Assert.Single(context.Targets);
    }

    [Fact]
    public void Resolve_SkipOnlineTrue_CachedSimbadIdentity_ResolvesWithZeroHttpCalls()
    {
        // Populate the "simbad" cache without creating a target (dryRun), so the second
        // call cannot short-circuit at step 2 and must reach the online stage.
        var onlineHandler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sim-script", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "::data::\nNGC 7635|HII|350.2|61.2\n");
            }
            return TextResponse(HttpStatusCode.OK, "id\n");
        });
        var seed = MakeResolver(onlineHandler).Resolve("Zzyzx Bubble Blob", createIfMissing: false, dryRun: true);
        Assert.Equal(TargetResolver.ResolutionStage.Simbad, seed.Stage);
        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, _cache.Get("simbad", "ZZYZX BUBBLE BLOB").Kind);

        var throwingHandler = ThrowingHandler();
        var result = MakeResolver(throwingHandler).Resolve("Zzyzx Bubble Blob", skipOnline: true);

        // The breaker suppresses the NETWORK, not the cache: an identity already in
        // catalog_cache still resolves, which is what makes ScanWriter's "the local cache
        // still applies" warning true rather than aspirational.
        Assert.Equal(TargetResolver.ResolutionStage.Simbad, result.Stage);
        Assert.NotNull(result.TargetId);
        Assert.True(result.Created);
        Assert.Equal(0, throwingHandler.CallCount);
    }

    [Fact]
    public void Resolve_NegativeCached_ReportedOnlyOnTheCallThatWroteTheRow()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host == "simbad.cds.unistra.fr"
            ? TextResponse(HttpStatusCode.OK, "::error::\nNo object found\n")
            : TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>"));
        var resolver = MakeResolver(handler);

        var first = resolver.Resolve("zzzz not real");
        var second = resolver.Resolve("zzzz not real");

        // The second call HIT the negative row rather than writing it, so it must not report
        // NegativeCached -- that is what keeps resolution_failed to one event per name.
        Assert.True(first.NegativeCached);
        Assert.False(second.NegativeCached);
    }

    [Fact]
    public void Resolve_TransientNetworkFailure_ReportsNegativeCachedFalse()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));

        var result = MakeResolver(handler).Resolve("also not real");

        Assert.True(result.TransientNetworkFailure);
        Assert.False(result.NegativeCached);
    }

    [Fact]
    public void Resolve_SkipOnlineTrue_ReportsNegativeCachedFalse()
    {
        var result = MakeResolver().Resolve("zzzz not real", skipOnline: true);

        Assert.False(result.NegativeCached);
        Assert.False(result.TransientNetworkFailure);
    }

    [Fact]
    public void Resolve_CreateIfMissingFalse_ReportsCreatedFalse()
    {
        var resolver = MakeResolver();

        var result = resolver.Resolve("M 31", createIfMissing: false, dryRun: true);

        Assert.False(result.Created);
        Assert.Null(result.TargetId);
    }

    // ---- Phase 7 Task 7: the solar-system step and ResolveIdentity ---------------------

    // A clean "queried, found nothing" from both online sources, which is the only condition
    // under which the pipeline may conclude anything durable about a name (spec 9.6).
    private static StubHttpMessageHandler NoMatchHandler() => new(request =>
        request.RequestUri!.Host == "simbad.cds.unistra.fr"
            ? TextResponse(HttpStatusCode.OK, "::error::\nNo object found\n")
            : TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>"));

    [Fact]
    public void Resolve_SolarSystemName_CreatesAUserDefinedTargetWithTheCategoryAsObjectType()
    {
        var result = MakeResolver(NoMatchHandler()).Resolve("Jupiter");

        Assert.True(result.Created);
        using var context = OpenContext();
        var target = Assert.Single(context.Targets);
        Assert.Equal(result.TargetId, target.Id);
        Assert.Equal("Jupiter", target.PrimaryName);
        Assert.Equal("Planet", target.ObjectType);
        Assert.True(target.UserDefined);
        // A planet has no fixed coordinates, and no bundled catalogue holds one.
        Assert.Null(target.CatalogId);
        Assert.Null(target.CatalogIdNormalized);
        Assert.Null(target.CommonName);
        Assert.Null(target.Ra);
        Assert.Null(target.Dec);
        Assert.Null(target.Constellation);
        Assert.Contains("JUPITER", target.Aliases);
        Assert.Empty(context.TargetCatalogMemberships);
    }

    // FIXER LIST item 4 end to end at the resolver: the stored object_type is what the
    // dashboard's Planet pill matches on.
    [Fact]
    public void Resolve_SolarSystemName_CategorizesUnderItsDashboardPill()
    {
        MakeResolver(NoMatchHandler()).Resolve("Jupiter");

        using var context = OpenContext();
        var target = Assert.Single(context.Targets);
        Assert.Equal("Planet", ObjectTypeCategories.Categorize(target.ObjectType));
        Assert.Contains("Planet", ObjectTypeCategories.SolarSystemCategories);
    }

    [Fact]
    public void Resolve_SolarSystemName_ReportsTheSolarSystemStage()
    {
        var result = MakeResolver(NoMatchHandler()).Resolve("C/2023 A3");

        Assert.Equal(TargetResolver.ResolutionStage.SolarSystem, result.Stage);
        Assert.NotNull(result.Identity);
        Assert.Equal("C/2023 A3", result.Identity.PrimaryName);
        Assert.Equal("Comet", result.Identity.ObjectType);
    }

    [Fact]
    public void Resolve_SolarSystemName_WritesNoNegativeCacheRow()
    {
        var result = MakeResolver(NoMatchHandler()).Resolve("Jupiter");

        // The name did not fail to resolve, it resolved here, so nothing about it is
        // remembered as unresolvable.
        Assert.False(result.NegativeCached);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "JUPITER").Kind);
    }

    [Fact]
    public void Resolve_SolarSystemName_Twice_CreatesOneTarget()
    {
        var resolver = MakeResolver(NoMatchHandler());

        var first = resolver.Resolve("Jupiter");
        // The second frame's name matches the created target at step 2, so it never reaches
        // the classifier again.
        var second = resolver.Resolve("Jupiter");
        // A second spelling of the same body links to the same row rather than colliding on
        // the unique primary_name index.
        var third = resolver.Resolve("Sol");
        var fourth = resolver.Resolve("The Sun");

        Assert.Equal(first.TargetId, second.TargetId);
        Assert.Equal(TargetResolver.ResolutionStage.Cache, second.Stage);
        Assert.Equal(third.TargetId, fourth.TargetId);
        using var context = OpenContext();
        Assert.Equal(2, context.Targets.Count());
        var sun = context.Targets.Single(t => t.PrimaryName == "Sun");
        Assert.Contains("SOL", sun.Aliases);
        Assert.Contains("THE SUN", sun.Aliases);
    }

    [Fact]
    public void Resolve_SolarSystemName_WithCreateIfMissingFalse_CreatesNothingButReportsTheIdentity()
    {
        var result = MakeResolver(NoMatchHandler()).Resolve("Jupiter", createIfMissing: false);

        Assert.Null(result.TargetId);
        Assert.False(result.Created);
        Assert.Equal(TargetResolver.ResolutionStage.SolarSystem, result.Stage);
        Assert.NotNull(result.Identity);
        Assert.Equal("Jupiter", result.Identity.PrimaryName);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
    }

    [Fact]
    public void Resolve_SolarSystemName_InDryRun_WritesNothing()
    {
        var result = MakeResolver(NoMatchHandler()).Resolve("Jupiter", createIfMissing: false, dryRun: true);

        Assert.Null(result.TargetId);
        Assert.Equal(TargetResolver.ResolutionStage.SolarSystem, result.Stage);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "JUPITER").Kind);
    }

    // Task 7 review ruling, reversing report D3: classification needs no network, so it runs
    // whatever the online stages did. Only the negative-cache write stays gated on the online
    // sources having actually been reached.
    [Fact]
    public void Resolve_SolarSystemName_TransientNetworkFailure_StillClassifies()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));

        var result = MakeResolver(handler).Resolve("Jupiter");

        Assert.Equal(TargetResolver.ResolutionStage.SolarSystem, result.Stage);
        Assert.True(result.Created);
        using var context = OpenContext();
        var target = Assert.Single(context.Targets);
        Assert.Equal("Planet", target.ObjectType);
        Assert.True(target.UserDefined);
    }

    [Fact]
    public void Resolve_SolarSystemName_WithTheCircuitBreakerOpen_StillClassifies()
    {
        // skipOnline is the per-scan breaker (spec 10.6). A throwing handler proves no request is
        // issued, and Jupiter's frames still get their target during that scan.
        var throwingHandler = ThrowingHandler();

        var result = MakeResolver(throwingHandler).Resolve("Jupiter", skipOnline: true);

        Assert.Equal(TargetResolver.ResolutionStage.SolarSystem, result.Stage);
        Assert.True(result.Created);
        Assert.Equal(0, throwingHandler.CallCount);
        using var context = OpenContext();
        Assert.Equal("Planet", Assert.Single(context.Targets).ObjectType);
    }

    [Fact]
    public void Resolve_UnknownName_WithTheCircuitBreakerOpen_IsStillNotNegativeCached()
    {
        // The other half of the same ruling: a name nothing classified and nobody asked the
        // network about is still not concluded unresolvable.
        var result = MakeResolver(ThrowingHandler()).Resolve("zzzz not real", skipOnline: true);

        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.False(result.NegativeCached);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "ZZZZ NOT REAL").Kind);
    }

    [Fact]
    public void Resolve_UnknownName_TransientNetworkFailure_IsStillNotNegativeCached()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));

        var result = MakeResolver(handler).Resolve("zzzz not real");

        Assert.True(result.TransientNetworkFailure);
        Assert.False(result.NegativeCached);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "ZZZZ NOT REAL").Kind);
    }

    // The one spelling of a stage, shared by ScanWriter's target_created details and the CLI
    // resolve verb (Task 7 review ruling). ToString().ToLowerInvariant() gave "solarsystem".
    [Theory]
    [InlineData(TargetResolver.ResolutionStage.Cache, "cache")]
    [InlineData(TargetResolver.ResolutionStage.Offline, "offline")]
    [InlineData(TargetResolver.ResolutionStage.Simbad, "simbad")]
    [InlineData(TargetResolver.ResolutionStage.Sesame, "sesame")]
    [InlineData(TargetResolver.ResolutionStage.Unresolved, "unresolved")]
    [InlineData(TargetResolver.ResolutionStage.SolarSystem, "solar_system")]
    public void StageName_IsSnakeCase(TargetResolver.ResolutionStage stage, string expected)
    {
        Assert.Equal(expected, TargetResolver.StageName(stage));
    }

    [Fact]
    public void Resolve_NameSimbadResolves_NeverReachesTheSolarSystemStep()
    {
        // "Vesta" is in the classifier's named-asteroid list, and SIMBAD answers for it here.
        // The classifier runs only after every catalogue produced nothing, so the catalogue
        // wins, which is the same ordering that keeps "47 Tuc" out of the asteroid branch.
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sim-script", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "::data::\nNGC 7635|HII|350.2|61.2\n");
            }
            return TextResponse(HttpStatusCode.OK, "id\n");
        });

        var result = MakeResolver(handler).Resolve("Vesta");

        Assert.Equal(TargetResolver.ResolutionStage.Simbad, result.Stage);
        using var context = OpenContext();
        var target = Assert.Single(context.Targets);
        Assert.False(target.UserDefined);
        Assert.NotEqual("Asteroid", target.ObjectType);
    }

    [Fact]
    public void ResolveIdentity_DoesNotConsultTheTargetsTable()
    {
        // An existing target carrying stale catalog fields under the name being re-resolved.
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Targets.Add(new Target
            {
                Id = Guid.NewGuid(),
                PrimaryName = "M 31",
                CatalogId = "STALE 1",
                CatalogIdNormalized = "STALE 1",
                ObjectType = "XYZ",
                Aliases = "[\"M 31\"]",
            });
            seed.SaveChanges();
        }

        var result = MakeResolver().ResolveIdentity("M 31");

        // Resolve would have stopped at step 2 and reported that row's own stale fields.
        Assert.Equal(TargetResolver.ResolutionStage.Offline, result.Stage);
        Assert.NotNull(result.Identity);
        Assert.Equal("M 31", result.Identity.CatalogId);
        Assert.NotEqual("XYZ", result.Identity.ObjectType);
    }

    [Fact]
    public void ResolveIdentity_WritesNoTargetRow()
    {
        var result = MakeResolver().ResolveIdentity("M 31");

        Assert.NotNull(result.Identity);
        Assert.Null(result.TargetId);
        Assert.False(result.Created);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
        Assert.Empty(context.TargetCatalogMemberships);
    }

    [Fact]
    public void ResolveIdentity_StillWritesCatalogCacheRows()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sim-script", StringComparison.Ordinal))
            {
                return TextResponse(HttpStatusCode.OK, "::data::\nNGC 7635|HII|350.2|61.2\n");
            }
            return TextResponse(HttpStatusCode.OK, "id\n");
        });

        var result = MakeResolver(handler).ResolveIdentity("Zzyzx Bubble Blob");

        Assert.Equal(TargetResolver.ResolutionStage.Simbad, result.Stage);
        // catalog_cache is app data, not target data, and spec 15 allows it on a read-only path.
        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, _cache.Get("simbad", "ZZYZX BUBBLE BLOB").Kind);
    }

    [Fact]
    public void ResolveIdentity_UnresolvableName_ReturnsNoIdentityAndWritesNoNegativeRow()
    {
        var result = MakeResolver(NoMatchHandler()).ResolveIdentity("zzzz not real");

        Assert.Null(result.Identity);
        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        Assert.False(result.NegativeCached);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "ZZZZ NOT REAL").Kind);
    }

    [Fact]
    public void ResolveIdentity_SolarSystemName_IsNotClassified()
    {
        // The solar-system step belongs to creation, not to reading what the catalogues say.
        var result = MakeResolver(NoMatchHandler()).ResolveIdentity("Jupiter");

        Assert.Null(result.Identity);
        Assert.Equal(TargetResolver.ResolutionStage.Unresolved, result.Stage);
        using var context = OpenContext();
        Assert.Empty(context.Targets);
    }

    [Fact]
    public void ResolveIdentity_RespectsSkipOnline()
    {
        var throwingHandler = ThrowingHandler();

        var result = MakeResolver(throwingHandler).ResolveIdentity("Zzyzx Bubble Blob", skipOnline: true);

        Assert.Null(result.Identity);
        Assert.Equal(0, throwingHandler.CallCount);
        Assert.False(result.TransientNetworkFailure);
    }
}
