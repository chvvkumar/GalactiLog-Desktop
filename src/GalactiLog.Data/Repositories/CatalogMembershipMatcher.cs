using System.Text.Json;
using GalactiLog.Core.Targets;

namespace GalactiLog.Data.Repositories;

// Port of catalog_base.match_ngc_catalog_for_target plus the Messier-membership rule and
// sac.enrich_target_from_sac (design-spec 9.7 step 5, 9.8; coordinator ruling Q8).
public static class CatalogMembershipMatcher
{
    // Idempotent via the unique (target_id, catalog_name) constraint already on
    // target_catalog_memberships (Phase 1 migration). Call after a target is inserted or
    // whenever its catalog_id/aliases change (e.g. the future "rebuild targets" action).
    public static void MatchForTarget(GalactiLogContext context, Entities.Target target)
    {
        var identifiers = BuildIdentifiers(target);

        if (identifiers.Count > 0)
        {
            foreach (var entry in context.StaticCatalogEntries.Where(e => e.NgcName != null).ToList())
            {
                var normalized = NameNormalizer.NormalizeNgcName(entry.NgcName!);
                if (identifiers.Contains(normalized))
                {
                    Upsert(context, target.Id, entry.CatalogName, entry.CatalogNumber, entry.Payload);
                }
            }
        }

        // Messier membership: reuses Task 4's lookup so this works whether CatalogId is
        // already "M n" or an "NGC n"/"IC n" form that happens to carry a Messier number.
        var openNgc = OfflineCatalogLookup.LookupOpenNgcForEnrichment(context, target.CatalogId);
        if (openNgc?.Messier is { } messier)
        {
            Upsert(context, target.Id, "messier", NameNormalizer.MessierNumber(messier), null);
        }

        context.SaveChanges();
    }

    // Coordinator ruling Q8: null-only fill of sac_description/sac_notes from the bundled SAC
    // catalog (static_catalog_entries where catalog_name = 'sac'), keyed by the same
    // identifier set MatchForTarget uses. Port of sac.enrich_target_from_sac, adapted to this
    // port's single static_catalog_entries table (the web app keeps a separate sac_catalog
    // table with its own object_name key; here a SAC row's NgcName/CatalogNumber both already
    // hold that same normalized designation, per Task 1's ParseSac).
    //
    // Mutates `target` only -- does not call SaveChanges. Callers (CreateOrLinkTarget) run
    // this immediately before MatchForTarget so the target insert, this enrichment, and the
    // membership rows all land in that method's one SaveChanges call, keeping the "single
    // SaveChanges per resolution" rule (review fix, item 8). Skipped for user-defined
    // targets, matching the Python source's own `user_defined` gate. No unique-violation retry
    // net: this only ever mutates an already-persisted row's nullable columns, never inserts.
    public static bool EnrichFromSac(GalactiLogContext context, Entities.Target target)
    {
        if (target.UserDefined)
        {
            return false;
        }

        var identifiers = BuildIdentifiers(target);
        if (identifiers.Count == 0)
        {
            return false;
        }

        Entities.StaticCatalogEntry? match = null;
        foreach (var entry in context.StaticCatalogEntries.Where(e => e.CatalogName == "sac" && e.NgcName != null).ToList())
        {
            if (identifiers.Contains(NameNormalizer.NormalizeNgcName(entry.NgcName!)))
            {
                match = entry;
                break;
            }
        }
        if (match is null)
        {
            return false;
        }

        using var payload = JsonDocument.Parse(match.Payload);
        var root = payload.RootElement;

        var updated = false;
        // StaticCatalogLoader.ParseSac (Task 1) writes the CSV "Notes" column under the JSON
        // key "notes" and the CSV "Other" column under "other". The web application's own
        // SACEntry maps CSV "Notes" to its `description` field and CSV "Other" to its `notes`
        // field (sac.py load_sac_csv), and enrich_target_from_sac fills sac_description from
        // `description` and sac_notes from `notes`. Matching that field-to-field behavior (not
        // this port's differently-named JSON keys) means sac_description is filled from the
        // "notes" JSON key and sac_notes from the "other" JSON key.
        if (target.SacDescription is null && GetNonEmptyString(root, "notes") is { } description)
        {
            target.SacDescription = description;
            updated = true;
        }
        if (target.SacNotes is null && GetNonEmptyString(root, "other") is { } notes)
        {
            target.SacNotes = notes;
            updated = true;
        }

        return updated;
    }

    private static string? GetNonEmptyString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var s = value.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static HashSet<string> BuildIdentifiers(Entities.Target target)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        if (target.CatalogId is { Length: > 0 })
        {
            identifiers.Add(target.CatalogId);
        }
        foreach (var alias in JsonSerializer.Deserialize<List<string>>(target.Aliases) ?? [])
        {
            if (!string.IsNullOrEmpty(alias))
            {
                identifiers.Add(alias);
            }
        }
        return identifiers;
    }

    // Checks the context's own pending (Added-but-not-yet-saved) entities first, then the DB.
    // MatchForTarget's single SaveChanges call means two static entries in the same catalog
    // that both match one target (e.g. two Arp rows whose NGC ids both land in the same
    // target's identifier set) must resolve to the SAME tracked row, not two separate `Add`s
    // that only collide when SaveChanges finally runs -- a DB-only query never sees an
    // Added-but-unsaved entity, so without this the second call here would insert a duplicate
    // and SaveChanges would throw on the (target_id, catalog_name) unique constraint (review
    // fix, item 5).
    private static void Upsert(GalactiLogContext context, Guid targetId, string catalogName, string catalogNumber, string? metadata)
    {
        var set = context.Set<Entities.TargetCatalogMembership>();
        var row = set.Local.FirstOrDefault(m => m.TargetId == targetId && m.CatalogName == catalogName)
            ?? set.SingleOrDefault(m => m.TargetId == targetId && m.CatalogName == catalogName);
        if (row is null)
        {
            row = new Entities.TargetCatalogMembership { TargetId = targetId, CatalogName = catalogName };
            set.Add(row);
        }
        row.CatalogNumber = catalogNumber;
        row.Metadata = metadata;
    }
}
