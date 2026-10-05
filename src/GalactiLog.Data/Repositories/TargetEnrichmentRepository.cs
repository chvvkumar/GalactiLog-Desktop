using System.Text.Json;
using GalactiLog.Core.Targets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

/// <summary>The outcome of <see cref="TargetEnrichmentRepository.ReEnrich"/>. Every failure is an
/// outcome rather than an exception, matching <see cref="RenameOutcome"/> and
/// <see cref="MergeStatus"/>, so Target detail reports it as a message instead of failing on the
/// UI thread.</summary>
public enum EnrichmentOutcome
{
    /// <summary>At least one column changed.</summary>
    Enriched,

    /// <summary>The identity matched what is already stored; nothing was written.</summary>
    Unchanged,

    /// <summary><c>user_defined</c> is set, so catalog enrichment is suppressed (spec 5.3).
    /// Nothing was written.</summary>
    Suppressed,

    NotFound,

    /// <summary>The rewrite would collide with another target's <c>primary_name</c> or
    /// <c>catalog_id_normalized</c>. Nothing was written.</summary>
    Conflict,
}

/// <summary>What a re-enrichment changed, for Target detail's status line.</summary>
public sealed record EnrichmentResult(EnrichmentOutcome Outcome, IReadOnlyList<string> FieldsChanged);

/// <summary>
/// Spec 9.7's creation steps 2 and 5 applied to a target that already exists: rewrite the catalog
/// columns from a freshly read identity, re-run OpenNGC enrichment, re-fill the SAC columns and
/// re-match catalog memberships. The writer FIXER LIST item 13 names, and the reason Target
/// detail's Re-resolve action can now change something.
/// </summary>
/// <remarks>
/// A sibling of <c>TargetWriteRepository</c> in <c>Data</c>, not an extension of it: it needs
/// <c>OfflineCatalogLookup</c> and <c>CatalogMembershipMatcher</c>, which are Data-layer
/// collaborators, where <c>TargetWriteRepository</c>'s three methods are each one row on one
/// table. Phase 7's App-layer writers of <c>targets</c> rows are therefore
/// <c>TargetWriteRepository</c> (rename and notes), <c>MergeRepository</c> (merge and unmerge)
/// and this type (catalog columns); nothing else writes that table outside the scan pipeline's
/// own <c>TargetResolver</c>.
/// </remarks>
public sealed class TargetEnrichmentRepository(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// Rewrites the catalog columns of <paramref name="targetId"/> from <paramref name="identity"/>
    /// and re-runs the two enrichment passes, in one tracking context and one save.
    /// </summary>
    /// <remarks>
    /// Three rules hold across every column written here:
    /// <list type="bullet">
    /// <item>a null in the identity never overwrites a stored non-null value, which is spec 9.7
    /// step 5's null-only fill applied to the whole set: an identity that has lost a field must
    /// not erase one the database already has;</item>
    /// <item><c>primary_name</c> is rewritten only when <c>name_locked</c> is false (spec 5.3:
    /// name_locked is "set when the user renames a target");</item>
    /// <item><c>notes</c>, <c>reference_thumbnail_path</c>, <c>merged_into_id</c>,
    /// <c>merged_at</c> and <c>name_locked</c> are never touched. Those are the user's, not the
    /// catalogue's.</item>
    /// </list>
    /// </remarks>
    public EnrichmentResult ReEnrich(Guid targetId, ResolvedIdentity identity)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString.Value, tracking: true));

        // MergedIntoId == null is the same exclusion every other lookup of `targets` applies
        // (spec 9.7: a merged-away row keeps its own columns and is excluded from every lookup).
        // Without it a loser row could be re-enriched into a name or catalog identity the winner
        // already carries, and the partial unique indexes would not stop it, because they are
        // scoped to unmerged rows. A merged-away id is NotFound for this writer: there is no
        // target there to enrich.
        var target = context.Targets.SingleOrDefault(row => row.Id == targetId && row.MergedIntoId == null);
        if (target is null)
        {
            return new EnrichmentResult(EnrichmentOutcome.NotFound, []);
        }

        // Spec 5.3: user_defined "suppresses catalog enrichment overwrites". This is what
        // protects a solar-system target (TargetResolver's SolarSystem stage) from being
        // flattened by a later catalogue lookup, and it is checked before anything is written
        // rather than relying on the two enrichment passes' own user_defined gates.
        if (target.UserDefined)
        {
            return new EnrichmentResult(EnrichmentOutcome.Suppressed, []);
        }

        var changed = new List<string>();

        if (!target.NameLocked)
        {
            Fill(target.PrimaryName, identity.PrimaryName, "primary name", changed,
                value => target.PrimaryName = value);
        }

        if (Fill(target.CatalogId, identity.CatalogId, "catalog id", changed,
                value => target.CatalogId = value))
        {
            // Derived from catalog_id, never from a separate source, so it is rewritten with it
            // and reported under the same name rather than as a second field.
            target.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId(target.CatalogId);
        }

        Fill(target.CommonName, identity.CommonName, "common name", changed,
            value => target.CommonName = value);
        Fill(target.Ra, identity.Ra, "RA", changed, value => target.Ra = value);
        Fill(target.Dec, identity.Dec, "Dec", changed, value => target.Dec = value);
        Fill(target.ObjectType, identity.ObjectType, "object type", changed,
            value => target.ObjectType = value);

        // The one alias union in the assembly (Task 2 owns it), so this cannot promise an alias
        // the merge would not add. An alias is never removed: a name that reached this target
        // once still reaches it.
        var (aliases, added) = MergeRepository.AbsorbAliases(target, identity.Aliases);
        if (added.Count > 0)
        {
            target.Aliases = JsonSerializer.Serialize(aliases);
            changed.Add("aliases");
        }

        // Spec 9.7 step 2. EnrichFromOpenNgc rewrites primary_name when it fills common_name, so
        // a locked name is restored immediately afterwards: the user's name survives, and the
        // other six columns it fills are kept.
        var lockedName = target.NameLocked ? target.PrimaryName : null;
        if (OfflineCatalogLookup.EnrichFromOpenNgc(context, target))
        {
            // One call fills constellation, both sizes, position angle, magnitude and surface
            // brightness, so it reports as one field rather than six.
            changed.Add("constellation");
        }
        if (lockedName is not null && !string.Equals(target.PrimaryName, lockedName, StringComparison.Ordinal))
        {
            target.PrimaryName = lockedName;
        }

        // Spec 9.7 step 5: SAC columns are a null-only fill (the matcher's own rule), and the
        // membership rows are idempotent by the unique (target_id, catalog_name) constraint.
        if (CatalogMembershipMatcher.EnrichFromSac(context, target))
        {
            changed.Add("SAC description");
        }

        try
        {
            // MatchForTarget saves, so this is the one save point for everything above: a
            // constraint violation rolls the whole thing back and nothing is half-written.
            CatalogMembershipMatcher.MatchForTarget(context, target);
            context.SaveChanges();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            // primary_name is unique among unmerged targets and catalog_id_normalized is unique
            // where merged_into_id is null (spec 5.3), so a rewrite onto another target's
            // identity comes back as an outcome rather than an exception, exactly as
            // TargetWriteRepository.Rename does.
            return new EnrichmentResult(EnrichmentOutcome.Conflict, []);
        }

        return new EnrichmentResult(
            changed.Count == 0 ? EnrichmentOutcome.Unchanged : EnrichmentOutcome.Enriched, changed);
    }

    // Writes `incoming` only when it has something to say and says something different. A null
    // incoming value is never written over a stored one; a null stored value is filled.
    private static bool Fill<T>(T? stored, T? incoming, string label, List<string> changed, Action<T> write)
    {
        if (incoming is null || EqualityComparer<T?>.Default.Equals(stored, incoming))
        {
            return false;
        }

        write(incoming);
        changed.Add(label);
        return true;
    }

    // SQLITE_CONSTRAINT, the same primary result code TargetWriteRepository.Rename matches.
    private const int SqliteConstraint = 19;
}
