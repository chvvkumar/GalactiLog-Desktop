using System.Text.Json;
using GalactiLog.Core.Targets;

namespace GalactiLog.Data.Repositories;

// The single entry point from a FITS OBJECT string to a target identity (design-spec 9.2's
// eight-step pipeline). Orchestrates every earlier Phase 3 task: OfflineCatalogLookup (Task
// 4), SimbadClient/SesameClient/AliasCurator/CatalogPriority/NameNormalizer (Tasks 2, 4, 5),
// CatalogCacheRepository (Task 6), TargetRepository and CatalogMembershipMatcher (this task).
public sealed class TargetResolver(
    string connectionString, string catalogsDirectory,
    CatalogCacheRepository cache, SimbadClient simbad, SesameClient sesame)
{
    // SolarSystem is Phase 7 Task 7's sixth value (FIXER LIST item 4): a name no catalogue
    // resolves, classified from its own text into one of spec 9.8's five solar-system categories.
    // Appended rather than inserted, so no existing value's ordinal moves.
    public enum ResolutionStage { Cache, Offline, Simbad, Sesame, Unresolved, SolarSystem }

    /// <summary>The stage's name as stored and printed: lower snake_case. The single spelling,
    /// shared by <c>ScanWriter</c>'s <c>target_created</c> details and the CLI <c>resolve</c>
    /// verb's <c>source</c> field.</summary>
    /// <remarks>
    /// Both callers derived their own before Phase 7 Task 7, which agreed while every value was
    /// one word and disagreed the moment <see cref="ResolutionStage.SolarSystem"/> arrived:
    /// <c>ToString().ToLowerInvariant()</c> gives "solarsystem", the CLI's switch gave
    /// "solar_system", and the two landed in the same database. One helper, so a seventh value
    /// cannot repeat it.
    /// </remarks>
    public static string StageName(ResolutionStage stage) => stage switch
    {
        ResolutionStage.SolarSystem => "solar_system",
        _ => stage.ToString().ToLowerInvariant(),
    };

    // Created and TransientNetworkFailure carry defaults so every existing construction
    // site and test stays source-compatible; only the paths that can actually insert a
    // target or consult the network pass them explicitly.
    //
    // TransientNetworkFailure is true only when an online source was consulted during THIS
    // call and exhausted its retries. It is the scan pipeline's circuit-breaker trigger
    // (spec 10.6, Phase 3 ruling): the first true flips the writer to skipOnline for the
    // rest of the run. A skipOnline call therefore always reports false -- nothing was
    // consulted, so nothing newly failed.
    //
    // NegativeCached is true only on the ONE call that actually wrote the "resolver"
    // negative row for this name. A later call that merely HITS that row reports false, so
    // ScanWriter's `resolution_failed` event fires once per name (spec 10.9: "resolved to
    // nothing and was negative-cached") instead of once per frame -- 50k frames sharing one
    // bad OBJECT string would otherwise write 50k identical activity rows.
    public sealed record ResolutionResult(
        Guid? TargetId, ResolutionStage Stage, ResolvedIdentity? Identity,
        bool Created = false, bool TransientNetworkFailure = false, bool NegativeCached = false);

    // Outcome of consulting one online source (SIMBAD or SESAME). Distinguishes "queried,
    // found nothing" (NoMatch) from "could not be queried" (Failed) -- conflating the two into
    // a single null used to make an exhausted-retries network failure look identical to a
    // genuine no-match, which could write a false "resolver" negative cache row for a name
    // that was never actually checked (review fix, item 1).
    private enum SourceOutcome { Found, NoMatch, Failed }

    private readonly record struct SourceResult(SourceOutcome Outcome, ResolvedIdentity? Identity);

    // createIfMissing is true for every real caller except Task 8's CLI `resolve` verb (spec
    // 15: "Read-only with respect to targets: it never creates one. It does write resolver
    // cache rows"). When false, every step through identity matching (steps 1-7, all
    // find-only) behaves identically; step 8's actual insert is skipped and `TargetId` comes
    // back null on a fresh identity that would otherwise have been created, while `Identity`
    // and `Stage` are still populated so the caller can print what WOULD have been created.
    // Cache writes (catalog_cache rows for simbad/sesame/resolver) happen exactly the same in
    // both modes -- they are app data, not target data, and spec 15 explicitly allows them.
    //
    // dryRun (review fix, item 4) is a stronger guarantee than createIfMissing alone: even
    // linking to an ALREADY-EXISTING target normally appends the incoming name as an alias and
    // calls SaveChanges on `targets` (spec 9.5 step 3). The CLI `resolve` verb must not write
    // to `targets` at all, so dryRun additionally suppresses that alias append and that
    // SaveChanges call. Resolver/simbad/sesame cache writes are unaffected by dryRun -- they
    // live in `catalog_cache`, not `targets`, and spec 15 explicitly allows them.
    //
    // ct (review ruling, item 14) cancels the online stage: a cancellation during a fetch or
    // during the retry backoff ends Resolve with OperationCanceledException and writes no
    // cache row, so a cancelled scan leaves nothing half-remembered. The offline stages are
    // local DB reads and finish in microseconds, so they are not checkpointed.
    // skipOnline is the per-scan circuit breaker (spec 10.6; Phase 3 ruling): after the
    // first transient network failure in a scan, ScanWriter passes true for every remaining
    // record so no HTTP request is issued for the rest of that run. Steps 4-5 still RUN, in
    // cache-only mode: an identity already sitting in catalog_cache under "simbad"/"sesame"
    // still resolves, which is what makes the breaker's "the local cache still applies"
    // warning true rather than aspirational. Offline catalog lookup and the existing-target
    // lookup are untouched.
    public ResolutionResult Resolve(string objectName, bool createIfMissing = true, bool dryRun = false, bool skipOnline = false, CancellationToken ct = default)
    {
        var normalized = NameNormalizer.Normalize(objectName);

        // An empty or whitespace-only OBJECT string has nothing to look up: every offline
        // pattern misses, and both online clients would issue a request with no name (SESAME
        // a GET with an empty query, SIMBAD a script whose sanitized name is blank). Stop here
        // instead, and write no cache row -- "" is not a name that can ever resolve, so there
        // is nothing to remember about it (review fix, whole-phase item 3).
        if (normalized.Length == 0)
        {
            return new ResolutionResult(null, ResolutionStage.Unresolved, null);
        }

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        var targets = new TargetRepository(context);

        // Step 1: pipeline-level negative cache. Only "resolver" rows are read here.
        if (IsNegativeCached(normalized))
        {
            return new ResolutionResult(null, ResolutionStage.Unresolved, null);
        }

        // Step 2: existing target lookup (spec 9.5). Identity is built from the existing
        // target's own stored fields (not re-derived) so a caller printing the result --
        // Task 8's CLI `resolve` verb -- has something to show regardless of which step
        // produced the match. Read-only: never mutates, so unaffected by dryRun.
        var existing = FindTargetByName(targets, objectName);
        if (existing is not null)
        {
            return new ResolutionResult(existing.Id, ResolutionStage.Cache, ToIdentity(existing));
        }

        // Steps 3, 4 and 5, shared with ResolveIdentity.
        var stages = RunCatalogStages(context, objectName, normalized, skipOnline, ct);
        var resolved = stages.Identity;
        var stage = stages.Stage;

        // Step 3's result: an offline hit skips the step-7 identity check, because
        // CreateOrLinkTarget does its own as part of section 9.7's creation order.
        if (resolved is not null && stage == ResolutionStage.Offline)
        {
            var (id, created) = CreateOrLinkTarget(context, targets, resolved, objectName, createIfMissing, dryRun);
            return new ResolutionResult(id, ResolutionStage.Offline, resolved, created);
        }

        if (resolved is null)
        {
            // Step 6a (FIXER LIST item 4, ruling Q13, Task 7 review ruling): a solar-system name
            // resolved by pattern, not by catalogue. Classification needs no network, so it runs
            // whatever the online stages did: a scan whose circuit breaker has tripped, or whose
            // SIMBAD call timed out, still files Jupiter's frames under Jupiter. It is a real
            // answer, so no negative row is written either: the name did not fail to resolve, it
            // resolved here.
            if (SolarSystemNames.Classify(objectName) is { } solar)
            {
                return CreateSolarSystemTarget(context, targets, solar, objectName, createIfMissing, dryRun);
            }

            // Step 6b: both online sources produced nothing. A resolved identity is NEVER
            // negative-cached -- only this "found absolutely nothing" branch writes here, and
            // only when every consulted source came back a clean NoMatch. If any source Failed
            // (exhausted its retries transiently), this name was never actually checked end to
            // end, so writing a negative here would poison the 7 day cache for a name that might
            // resolve fine once the network is back (coordinator ruling Q7; review fix, item 1).
            // ... and the same reasoning applies, for a different reason, when the circuit
            // breaker skipped the online sources entirely: this name was never checked at all
            // in this run, so a negative row here would poison the 7 day cache for a name that
            // resolves fine the moment the network is back. This is the one conclusion that stays
            // gated on the online sources having actually been reached; the classifier above does
            // not, because it asks nothing of them.
            var negativeCached = stages.CheckedOnline && !stages.AnySourceFailed;
            if (negativeCached)
            {
                cache.Save("resolver", normalized, null);
            }
            return new ResolutionResult(null, ResolutionStage.Unresolved, null,
                Created: false, TransientNetworkFailure: stages.AnySourceFailed, NegativeCached: negativeCached);
        }

        // Step 7: identity match against an existing target. Identity reported is the
        // existing target's own stored fields (ToIdentity), matching step 2's convention,
        // not the freshly-fetched `resolved` payload.
        var matched = MatchTargetByIdentity(targets, resolved, objectName, dryRun);
        if (matched is not null)
        {
            if (!dryRun)
            {
                context.SaveChanges(); // persists the alias MatchTargetByIdentity may have appended.
            }
            return new ResolutionResult(matched.Id, stage, ToIdentity(matched));
        }

        // Step 8: create. identityAlreadyMatched: the check just above came back null and
        // enrichment cannot change what that check keys on (catalog_id, then the incoming
        // name), so CreateOrLinkTarget's own internal check would repeat a full alias-table
        // scan with JSON deserialization for a guaranteed second null (review fix, item 2).
        var (createdId, wasCreated) = CreateOrLinkTarget(context, targets, resolved, objectName, createIfMissing, dryRun, identityAlreadyMatched: true);
        return new ResolutionResult(createdId, stage, resolved, wasCreated);
    }

    /// <summary>
    /// Steps 1, 3, 4 and 5 of the section 9.2 pipeline with the existing-target lookup and every
    /// write to <c>targets</c> removed: the identity the catalogues currently give for a name,
    /// regardless of what is already stored. This is what re-enrichment needs, and it is why
    /// <see cref="Resolve"/> alone could not provide it (FIXER LIST item 13).
    /// </summary>
    /// <remarks>
    /// Writes <c>catalog_cache</c> rows exactly as <see cref="Resolve"/> does: those are app data,
    /// not target data, and spec 15 already allows them on a read-only path.
    /// <para>
    /// It writes no negative cache row and does not run the solar-system step: it is a read of
    /// what the catalogues say, and a caller re-enriching an existing target has already decided
    /// the target exists.
    /// </para>
    /// </remarks>
    public ResolutionResult ResolveIdentity(string objectName, bool skipOnline = false, CancellationToken ct = default)
    {
        var normalized = NameNormalizer.Normalize(objectName);
        if (normalized.Length == 0 || IsNegativeCached(normalized))
        {
            return new ResolutionResult(null, ResolutionStage.Unresolved, null);
        }

        // Non-tracking, unlike Resolve's context: nothing here may write to `targets`, and the
        // cheapest way to guarantee that is to hold no entity this method could modify.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString));
        var stages = RunCatalogStages(context, objectName, normalized, skipOnline, ct);

        return stages.Identity is null
            ? new ResolutionResult(null, ResolutionStage.Unresolved, null,
                TransientNetworkFailure: stages.AnySourceFailed)
            : new ResolutionResult(null, stages.Stage, stages.Identity,
                TransientNetworkFailure: stages.AnySourceFailed);
    }

    // What steps 3 to 5 produced: the identity (null when nothing did), the stage that produced
    // it, and the two flags step 6 needs to decide whether this name was checked end to end.
    private readonly record struct CatalogStageResult(
        ResolvedIdentity? Identity, ResolutionStage Stage, bool CheckedOnline, bool AnySourceFailed);

    // Steps 3, 4 and 5, the whole of the pipeline that asks a catalogue rather than the `targets`
    // table. The second caller (ResolveIdentity) is why this is a method rather than a stretch of
    // Resolve's body: two copies of the offline-then-SIMBAD-then-SESAME order would eventually
    // answer differently for the same name (design-lessons rule 1). Step 1 stays in the two
    // callers because spec 9.2 puts the existing-target lookup between it and step 3, and
    // ResolveIdentity deliberately skips that lookup.
    private CatalogStageResult RunCatalogStages(
        GalactiLogContext context, string objectName, string normalized, bool skipOnline, CancellationToken ct)
    {
        // Step 3: offline catalog lookup (Task 4), offline-first. Skips online entirely on an
        // exact hit.
        var offline = OfflineCatalogLookup.Lookup(context, objectName, catalogsDirectory);
        if (offline is not null)
        {
            return new CatalogStageResult(offline, ResolutionStage.Offline, !skipOnline, false);
        }

        // Steps 4-5: SIMBAD, then SESAME only if SIMBAD produced nothing (whether that is a
        // clean no-match or a network failure -- either way SESAME still deserves a try).
        // CheckedOnline is false exactly when the circuit breaker is open: the cache is
        // still consulted, but nothing above may claim this name was CHECKED and found
        // absent, because no source was actually queried.
        var queryName = OfflineCatalogLookup.RewriteForOnlineQuery(objectName, catalogsDirectory);
        var simbadResult = ResolveViaSimbad(normalized, objectName, queryName, skipOnline, ct);
        var stage = ResolutionStage.Simbad;
        var resolved = simbadResult.Outcome == SourceOutcome.Found ? simbadResult.Identity : null;
        var anySourceFailed = simbadResult.Outcome == SourceOutcome.Failed;

        if (resolved is null)
        {
            var sesameResult = ResolveViaSesame(normalized, objectName, skipOnline, ct);
            stage = ResolutionStage.Sesame;
            if (sesameResult.Outcome == SourceOutcome.Found)
            {
                resolved = sesameResult.Identity;
            }
            else if (sesameResult.Outcome == SourceOutcome.Failed)
            {
                anySourceFailed = true;
            }
        }

        return new CatalogStageResult(resolved, stage, !skipOnline, anySourceFailed);
    }

    // Step 1, shared by both entry points. Only "resolver" rows are read (spec 9.2).
    private bool IsNegativeCached(string normalized)
        => cache.Get("resolver", normalized).Kind == CatalogCacheRepository.CacheHit.Negative;

    // Spec 9.8's five solar-system categories, created as a user_defined target (FIXER LIST item
    // 4, ruling Q13). No catalog id, no coordinates and no enrichment of any kind: a planet has no
    // fixed coordinates, writing one would be worse than writing none, and no bundled catalogue
    // holds a solar-system body. user_defined is spec 5.3's "suppresses catalog enrichment
    // overwrites", which is exactly right here: no catalogue will ever have a better answer for
    // Jupiter.
    //
    // createIfMissing false (the CLI `resolve` verb, spec 15) and dryRun suppress the insert
    // exactly as they do at step 8, and the result still reports the identity and the stage so the
    // CLI can print what would have been created.
    private static ResolutionResult CreateSolarSystemTarget(
        GalactiLogContext context, TargetRepository targets,
        (string Category, string PrimaryName) solar, string objectName, bool createIfMissing, bool dryRun)
    {
        var clean = NameNormalizer.StripPanel(NameNormalizer.Normalize(objectName));
        var aliases = clean.Length > 0 ? new List<string> { clean } : [];
        var identity = new ResolvedIdentity(
            solar.PrimaryName, null, null, null, null, solar.Category, aliases);

        // Two names can classify to one display name ("Sol" and "Sun", "The Moon" and "Moon"), and
        // primary_name is unique among unmerged targets. The second name links to the target the
        // first created and contributes its own alias, rather than colliding on the insert.
        var existing = targets.FindByPrimaryName(solar.PrimaryName);
        if (existing is not null)
        {
            if (!dryRun)
            {
                if (clean.Length > 0)
                {
                    targets.AddAliasIfMissing(existing, clean);
                }
                context.SaveChanges();
            }
            return new ResolutionResult(existing.Id, ResolutionStage.SolarSystem, ToIdentity(existing));
        }

        if (!createIfMissing || dryRun)
        {
            return new ResolutionResult(null, ResolutionStage.SolarSystem, identity);
        }

        var target = new Entities.Target
        {
            Id = Guid.NewGuid(),
            PrimaryName = solar.PrimaryName,
            ObjectType = solar.Category,
            Aliases = JsonSerializer.Serialize(aliases),
            UserDefined = true,
        };
        targets.Insert(target);
        context.SaveChanges();

        return new ResolutionResult(target.Id, ResolutionStage.SolarSystem, identity, Created: true);
    }

    // Spec 9.5. Every lookup excludes merged-away targets (enforced inside TargetRepository).
    private static Entities.Target? FindTargetByName(TargetRepository targets, string objectName)
    {
        var normalized = NameNormalizer.Normalize(objectName);
        var hit = targets.FindByAliasExact(normalized);
        if (hit is not null)
        {
            return hit;
        }

        var stripped = NameNormalizer.StripPanel(normalized);
        if (stripped != normalized)
        {
            hit = targets.FindByAliasExact(stripped);
            if (hit is not null)
            {
                return hit;
            }
        }

        return targets.FindByPrimaryName(NameNormalizer.NormalizeDisplay(objectName));
    }

    // Spec 9.5. Identity first (catalog_id_normalized), then name fallback. On a match,
    // appends the panel-stripped, uppercase incoming name as an alias if not already present
    // (case-insensitively, per TargetRepository.AddAliasIfMissing) -- unless dryRun, in which
    // case this method still FINDS the target but performs no mutation at all (review fix,
    // item 4). Never creates.
    // Internal, not private, since Phase 14B Task 4: CatalogIdentityBackfill needs exactly this
    // rule and a second copy of it would let the backfill link frames the scan would not
    // (design-lessons rule 1). A visibility widening only; the body is unchanged.
    internal static Entities.Target? MatchTargetByIdentity(TargetRepository targets, ResolvedIdentity resolved, string objectName, bool dryRun = false)
    {
        var catNorm = NameNormalizer.NormalizeCatalogId(resolved.CatalogId);
        var target = catNorm is not null ? targets.FindByCatalogIdNormalized(catNorm) : null;
        target ??= FindTargetByName(targets, objectName);

        if (target is not null && !dryRun)
        {
            var incoming = NameNormalizer.StripPanel(NameNormalizer.Normalize(objectName));
            if (incoming.Length > 0)
            {
                targets.AddAliasIfMissing(target, incoming);
            }
        }

        return target;
    }

    // Spec 9.7 steps 1-5. Called both directly from an offline hit (Resolve's step 3), where
    // no identity check has run yet and this method performs 9.7's own (step 3 of 9.7), and
    // after a step-7 identity-match miss (Resolve's step 8), where identityAlreadyMatched
    // suppresses that check as an exact repeat of one that just returned null.
    //
    // Returns null, not a new target's id, when createIfMissing is false (or dryRun is true)
    // and no existing target already carries this identity (Task 8's CLI `resolve` verb; spec
    // 15). dryRun additionally suppresses the SaveChanges call on the "link to existing"
    // branch and blocks the insert branch entirely regardless of createIfMissing, since the
    // CLI's read-only mode must not write to `targets` at all (review fix, item 4).
    // Created (the second tuple element) is true only on the step-4 insert branch, so
    // ScanWriter can emit exactly one `target_created` activity row per real insert
    // (spec 10.9) without re-querying to find out whether the id it got back is new.
    private static (Guid? Id, bool Created) CreateOrLinkTarget(GalactiLogContext context, TargetRepository targets, ResolvedIdentity resolved, string objectName, bool createIfMissing, bool dryRun = false, bool identityAlreadyMatched = false)
    {
        var clean = NameNormalizer.StripPanel(NameNormalizer.Normalize(objectName));
        var aliases = new List<string>(resolved.Aliases);
        if (clean.Length > 0 && !aliases.Any(a => a.ToUpperInvariant() == clean))
        {
            aliases.Add(clean);
        }

        var target = new Entities.Target
        {
            Id = Guid.NewGuid(),
            PrimaryName = resolved.PrimaryName,
            CatalogId = resolved.CatalogId,
            CatalogIdNormalized = NameNormalizer.NormalizeCatalogId(resolved.CatalogId),
            CommonName = resolved.CommonName,
            Aliases = JsonSerializer.Serialize(aliases),
            Ra = resolved.Ra,
            Dec = resolved.Dec,
            ObjectType = resolved.ObjectType,
        };

        // Step 2: enrich before any match/insert, so primary_name is final. Enrichment never
        // changes CatalogId, so CatalogIdNormalized computed above stays valid. `target` here
        // is a plain in-memory object, not yet added to the context, so this is safe even in
        // dry-run mode: nothing about it is tracked or written until targets.Insert() below,
        // which dryRun never reaches.
        OfflineCatalogLookup.EnrichFromOpenNgc(context, target);

        // Step 3: match by the final identity. Skipped when the caller (Resolve's step 7) has
        // already run exactly this check and got null: enrichment above rewrites only
        // PrimaryName and CommonName, never CatalogId, and the check keys on CatalogId then on
        // the incoming name, so a repeat is guaranteed to return null too (review fix, item 2).
        var existing = identityAlreadyMatched
            ? null
            : MatchTargetByIdentity(targets, resolved with { PrimaryName = target.PrimaryName, CatalogId = target.CatalogId, CommonName = target.CommonName }, objectName, dryRun);
        if (existing is not null)
        {
            // Persists only the alias MatchTargetByIdentity may have appended to the existing
            // target. `target` above is a detached in-memory object that is never inserted on
            // this branch, so nothing OfflineCatalogLookup.EnrichFromOpenNgc wrote to it is
            // persisted by this call (review fix, item 7).
            if (!dryRun)
            {
                context.SaveChanges();
            }
            return (existing.Id, false);
        }

        if (!createIfMissing || dryRun)
        {
            return (null, false);
        }

        // Step 4: insert. NO IntegrityError retry net -- resolution runs only on the single
        // scan writer task or the CLI's single-threaded resolve verb (spec 9.7), so a unique
        // violation here is a genuine bug, not an expected race, and must propagate rather
        // than being swallowed.
        targets.Insert(target);

        // Step 5: SAC enrichment (coordinator ruling Q8) then catalog memberships. The insert
        // above is left unsaved on purpose so all three land in MatchForTarget's single
        // SaveChanges -- one round trip, and a failure anywhere in step 5 leaves no
        // half-created target behind. EF orders the target row before the membership rows
        // that carry its foreign key (review fix, item 8).
        CatalogMembershipMatcher.EnrichFromSac(context, target);
        CatalogMembershipMatcher.MatchForTarget(context, target);

        return (target.Id, true);
    }

    private static ResolvedIdentity ToIdentity(Entities.Target target)
        => new(target.PrimaryName, target.CatalogId, target.CommonName, target.Ra, target.Dec, target.ObjectType,
            JsonSerializer.Deserialize<List<string>>(target.Aliases) ?? []);

    private static string? Serialize(CachedIdentityPayload payload) => JsonSerializer.Serialize(payload);
    private static CachedIdentityPayload Deserialize(string json) => JsonSerializer.Deserialize<CachedIdentityPayload>(json)!;

    // Raw, uncurated shape cached under "simbad"/"sesame" -- curation (BuildPrimaryName,
    // CurateAliases, ExtractCommonName) is re-applied on every read, cached or fresh, exactly
    // like curate_simbad_result(raw) is always called in the Python source. fits_names is
    // never passed at this call site in the reference (resolve_target_name_cached calls
    // curate_simbad_result(orig_cached) with no fits_names argument), so this port does not
    // pass any either -- the "append the panel-stripped incoming name" step happens once,
    // later, in CreateOrLinkTarget, matching _create_target's own separate manual append.
    // Internal rather than private only because Curate below is (accessibility consistency
    // requires a parameter type at least as visible as its method). The shape is unchanged.
    internal sealed record CachedIdentityPayload(string MainId, string? ObjectType, double? Ra, double? Dec, IReadOnlyList<string> Aliases);

    // Internal, not private, since Phase 14B Task 4: SmartRebuild's fourth pass re-derives an
    // identity through spec 9.4.5's curation and a second copy of it would name a target something
    // no resolution ever would (design-lessons rule 1). A visibility widening only.
    internal static ResolvedIdentity Curate(CachedIdentityPayload raw)
    {
        var catalogId = CatalogPriority.ExtractCatalogId(raw.Aliases, raw.MainId);
        var curatedAliases = AliasCurator.CurateAliases(raw.Aliases);
        var commonName = AliasCurator.ExtractCommonName(raw.Aliases);
        var primaryName = AliasCurator.BuildPrimaryName(catalogId, commonName);
        return new ResolvedIdentity(primaryName, catalogId, commonName, raw.Ra, raw.Dec, raw.ObjectType, curatedAliases);
    }

    // Whatever HttpClient.Send/the clients' own EnsureSuccessStatusCode calls can throw for a
    // transient condition (timeout, connection failure, 5xx, 429) -- Task 5's clients throw
    // NonTransientCatalogException explicitly for the one non-transient case (a non-429 4xx),
    // which is handled separately and re-thrown, never treated as transient. Deliberately
    // narrow: JsonException (a real deserialization bug) and DbUpdateException (a real DB
    // failure inside the cache repository's own SaveChanges) must propagate uncaught rather
    // than being mistaken for "the network is flaky" (review fix, item 1).
    // A requested cancellation disqualifies everything: OperationCanceledException (and the
    // TaskCanceledException HttpClient raises for it) would otherwise look exactly like the
    // timeout case and be swallowed into a "this source produced nothing" outcome, when the
    // caller asked to stop and expects the exception (review ruling, item 14).
    private static bool IsTransientNetworkFailure(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or OperationCanceledException;

    // Spec 9.6's SIMBAD dual-key case. mappedNorm is null when the rewritten query name
    // normalizes to the same key as the original (nothing distinct to check).
    // cacheOnly (the scan's circuit breaker) hands GetOrFetchDualKey its existing skipSimbad
    // path: both cache keys are still read, but a miss stays a miss and neither fetch
    // delegate is ever invoked.
    private SourceResult ResolveViaSimbad(string normalized, string objectName, string queryName, bool cacheOnly, CancellationToken ct)
    {
        string? mappedNorm = null;
        if (!string.Equals(queryName, objectName, StringComparison.Ordinal))
        {
            var candidate = NameNormalizer.Normalize(queryName);
            mappedNorm = candidate == normalized ? null : candidate;
        }

        string? payload;
        try
        {
            payload = cache.GetOrFetchDualKey(
                "simbad", normalized, mappedNorm,
                fetchMapped: () => mappedNorm is not null ? FetchSimbadRaw(queryName, ct) : null,
                fetchOriginal: () => FetchSimbadRaw(objectName, ct),
                skipSimbad: cacheOnly,
                ct: ct);
        }
        catch (NonTransientCatalogException)
        {
            throw; // a genuine client-side error querying SIMBAD -- not swallowed.
        }
        catch (Exception ex) when (IsTransientNetworkFailure(ex, ct))
        {
            // Every retry attempt exhausted transiently (Task 6 lets the final attempt's
            // exception propagate rather than negative-caching a network hiccup). Treat
            // SIMBAD as having FAILED (not "found nothing") for this resolution; SESAME still
            // gets a try, and step 6 must not treat this as a checked-and-clean no-match.
            return new SourceResult(SourceOutcome.Failed, null);
        }

        return payload is null
            ? new SourceResult(SourceOutcome.NoMatch, null)
            : new SourceResult(SourceOutcome.Found, Curate(Deserialize(payload)));
    }

    private string? FetchSimbadRaw(string name, CancellationToken ct)
    {
        var raw = simbad.QueryObject(name, ct);
        if (raw is null)
        {
            return null;
        }
        var aliases = simbad.QueryAliases(raw.MainId, ct);
        return Serialize(new CachedIdentityPayload(raw.MainId, raw.ObjectType, raw.Ra, raw.Dec, aliases));
    }

    private SourceResult ResolveViaSesame(string normalized, string objectName, bool cacheOnly, CancellationToken ct)
    {
        if (cacheOnly)
        {
            // Cache read only, no fetch and no cache write (spec 9.6's cache-only mode).
            var hit = cache.GetCacheOnly("sesame", normalized);
            return hit is null
                ? new SourceResult(SourceOutcome.NoMatch, null)
                : new SourceResult(SourceOutcome.Found, Curate(Deserialize(hit)));
        }

        string? payload;
        try
        {
            payload = cache.GetOrFetch("sesame", normalized, () => FetchSesameRaw(objectName, ct), ct);
        }
        catch (NonTransientCatalogException)
        {
            throw;
        }
        catch (Exception ex) when (IsTransientNetworkFailure(ex, ct))
        {
            return new SourceResult(SourceOutcome.Failed, null);
        }

        return payload is null
            ? new SourceResult(SourceOutcome.NoMatch, null)
            : new SourceResult(SourceOutcome.Found, Curate(Deserialize(payload)));
    }

    private string? FetchSesameRaw(string objectName, CancellationToken ct)
    {
        var raw = sesame.Query(objectName, ct);
        return raw is null ? null : Serialize(new CachedIdentityPayload(raw.MainId, raw.ObjectType, raw.Ra, raw.Dec, raw.Aliases));
    }
}
