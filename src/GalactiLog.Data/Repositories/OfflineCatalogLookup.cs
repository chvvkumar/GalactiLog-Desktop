using System.Text.RegularExpressions;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;

namespace GalactiLog.Data.Repositories;

// Offline-first target resolution (design-spec 9.2 step 3, 9.3 in full) against the bundled
// catalogs Task 1 loads and the name maps Task 3 provides. No network. Static because every
// method takes the GalactiLogContext it needs as a parameter: Task 7's TargetResolver owns
// one context per Resolve() call and passes it down to every step (ponytail: one lookup
// class, LINQ queries, no repository interface).
public static class OfflineCatalogLookup
{
    private static readonly Regex MessierPattern = new(@"^M\s*(\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex NgcIcPattern = new(@"^(NGC|IC)\s*0*(\d+)([A-Z]?)$", RegexOptions.IgnoreCase);
    private static readonly Regex CaldwellPattern = new(@"^(?:Caldwell|C)\s+(\d+)$", RegexOptions.IgnoreCase);

    // Deliberately only the "Abell N" alternative of CatalogPriority pattern 5, never
    // "PN A66 N": the bundled abell.csv is George Abell's GALAXY CLUSTER catalog (columns
    // richness_class/distance_class/bm_type/redshift), not his separate ~86-entry planetary
    // nebula catalog. No PN A66 data is bundled at all, so a "PN A66 N" input correctly never
    // matches here and falls through to the online stage untouched.
    private static readonly Regex AbellPattern = new(@"^Abell\s+(\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex ArpPattern = new(@"^Arp\s*(\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex Sh2Rewrite = new(@"^SH?\s*2[\s\-_]+(\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex LbnRewrite = new(@"^LBN[\s\-_]+(\d+)$", RegexOptions.IgnoreCase);

    // Spec 9.3's four-step order. Returns null if no bundled catalog can produce an identity;
    // never throws for an unrecognized name; never touches the network.
    public static ResolvedIdentity? Lookup(GalactiLogContext context, string objectName, string catalogsDirectory)
    {
        var hit = TryDirectDesignation(context, objectName);
        if (hit is not null) return hit;

        var stripped = NameNormalizer.StripPanel(objectName).Trim();
        var mapped = MapCommonName(stripped, catalogsDirectory);
        if (mapped is not null)
        {
            hit = TryDirectDesignation(context, mapped);
            if (hit is not null) return hit;
        }

        if (stripped.Contains(" - "))
        {
            var baseName = stripped.Split(" - ", 2)[0].Trim();
            var baseMapped = MapCommonName(baseName, catalogsDirectory);
            if (baseMapped is not null)
            {
                hit = TryDirectDesignation(context, baseMapped);
                if (hit is not null) return hit;
            }
            hit = TryDirectDesignation(context, baseName);
            if (hit is not null) return hit;
        }

        // Step 4 (Sharpless/LBN rewrite) never matches a bundled table -- no sharpless.csv or
        // lbn.csv is shipped -- so it cannot produce a hit here. See RewriteForOnlineQuery,
        // which applies the same rewrite for the online stage's benefit.
        return null;
    }

    // Port of simbad._get_simbad_id: NOT a DB lookup. Produces the best SIMBAD-resolvable
    // string for Task 5/7's online stage to query, whether or not Lookup() above found
    // anything offline. Mirrors _get_simbad_id's exact step order and its final fallback
    // (the ORIGINAL untouched objectName, not the panel-stripped intermediate).
    public static string RewriteForOnlineQuery(string objectName, string catalogsDirectory)
    {
        var stripped = NameNormalizer.StripPanel(objectName).Trim();

        var mapped = MapCommonName(stripped, catalogsDirectory);
        if (mapped is not null) return mapped;

        var baseName = stripped;
        if (baseName.Contains(" - "))
        {
            var candidate = baseName.Split(" - ", 2)[0].Trim();
            var baseMapped = MapCommonName(candidate, catalogsDirectory);
            if (baseMapped is not null) return baseMapped;
            baseName = candidate;
        }

        var sh2 = Sh2Rewrite.Match(baseName);
        if (sh2.Success) return $"SH 2-{sh2.Groups[1].Value}";

        var lbn = LbnRewrite.Match(baseName);
        if (lbn.Success) return $"LBN {lbn.Groups[1].Value}";

        return objectName;
    }

    private static string? MapCommonName(string strippedName, string catalogsDirectory)
    {
        var key = strippedName.ToLowerInvariant();
        if (CommonNameOverrides.Map.TryGetValue(key, out var overrideHit)) return overrideHit;
        return StellariumNames.GetNames(catalogsDirectory).TryGetValue(key, out var stellariumHit) ? stellariumHit : null;
    }

    private static ResolvedIdentity? TryDirectDesignation(GalactiLogContext context, string candidate)
    {
        var display = NameNormalizer.NormalizeDisplay(candidate).Trim();

        var messier = MessierPattern.Match(display);
        if (messier.Success)
        {
            var key = $"M {messier.Groups[1].Value.PadLeft(3, '0')}";
            var row = context.OpenNgcCatalogEntries.FirstOrDefault(e => e.Messier == key);
            if (row is not null) return BuildIdentity(row, $"M {int.Parse(messier.Groups[1].Value)}");
        }

        if (NgcIcPattern.IsMatch(display))
        {
            var normalized = NameNormalizer.NormalizeNgcName(display);
            var row = context.OpenNgcCatalogEntries.SingleOrDefault(e => e.Name == normalized);
            if (row is not null) return BuildIdentity(row, normalized);
        }

        var caldwell = CaldwellPattern.Match(display);
        if (caldwell.Success)
        {
            var hit = FollowStaticToOpenNgc(context, "caldwell", $"C{int.Parse(caldwell.Groups[1].Value)}");
            if (hit is not null) return hit;
        }

        var abell = AbellPattern.Match(display);
        if (abell.Success)
        {
            // Always returns null today: Task 1's ParseAbell stores NgcName = null for
            // every row (abell.csv has no NGC/IC crosswalk column). Kept for spec fidelity
            // and in case a future catalog update adds one.
            var hit = FollowStaticToOpenNgc(context, "abell", $"Abell {int.Parse(abell.Groups[1].Value)}");
            if (hit is not null) return hit;
        }

        var arp = ArpPattern.Match(display);
        if (arp.Success)
        {
            var hit = FollowStaticToOpenNgc(context, "arp", $"Arp {int.Parse(arp.Groups[1].Value)}");
            if (hit is not null) return hit;
        }

        return null;
    }

    // Spec 9.3 step 1's "then follow its ngc_name into openngc_catalog": Caldwell, Abell, and
    // Arp entries in static_catalog_entries carry no RA/Dec/type of their own (abell.csv is
    // the exception with ra/dec, but those describe the CLUSTER's own catalog entry, not an
    // OpenNGC row, and spec 9.3 does not direct using them as a target identity) -- the full
    // identity always comes from the OpenNGC row the static entry's NgcName points at. A
    // static row with a null or unresolvable NgcName produces no offline identity at all.
    private static ResolvedIdentity? FollowStaticToOpenNgc(GalactiLogContext context, string catalogName, string catalogNumber)
    {
        var staticRow = context.StaticCatalogEntries.SingleOrDefault(e => e.CatalogName == catalogName && e.CatalogNumber == catalogNumber);
        if (staticRow?.NgcName is not { } ngc)
        {
            return null;
        }
        var normalized = NameNormalizer.NormalizeNgcName(ngc);
        var openNgc = context.OpenNgcCatalogEntries.SingleOrDefault(e => e.Name == normalized);
        return openNgc is null ? null : BuildIdentity(openNgc, normalized);
    }

    // Review fix (Phase 3 Task 7 coordinator review, item 3): CatalogPriority ranks Messier
    // (index 0) above NGC/IC (indices 1-2), so a row reached via its NGC/IC designation but
    // that also carries a Messier number must still report the Messier form here --
    // otherwise Resolve("NGC 224") and Resolve("M 31") would normalize to different
    // catalog_id_normalized values and create two targets for the same object, instead of
    // converging on one the way the online SIMBAD path already does via ExtractCatalogId.
    private static ResolvedIdentity BuildIdentity(Entities.OpenNgcCatalogEntry row, string catalogId)
    {
        // The designation the caller reached this row by, before a Messier rewrite below can
        // replace it. It must survive into Aliases: for a Messier-form input ("M 57") the
        // catalog id becomes "M 57" while the row's own NGC/IC name ("NGC 6720") is the only
        // identifier the SAC and static-catalog membership tables are keyed by, so dropping it
        // here left Messier-form input with no NGC designation to match on (review fix,
        // whole-phase item 1).
        var rowName = row.Name;

        if (row.Messier is { } messier)
        {
            catalogId = $"M {NameNormalizer.MessierNumber(messier)}";
        }

        var commonName = OpenNgcCatalog.ExtractCommonName(row.CommonNames);
        return new ResolvedIdentity(
            PrimaryName: AliasCurator.BuildPrimaryName(catalogId, commonName),
            CatalogId: catalogId,
            CommonName: commonName,
            Ra: row.Ra,
            Dec: row.Dec,
            // OpenNGC's own Type vocabulary ("G", "OCl", "GCl", "PN", "Neb", "SNR", "*",
            // "**", ...) is NOT the same vocabulary as SIMBAD's OTYPELIST that
            // ObjectTypeCategories.Categorize (Task 7, spec 9.8) is built against. Per
            // coordinator ruling Q6, ObjectTypeCategories.Categorize accepts OpenNGC codes
            // directly as extra table rows (no translation layer), so storing the OpenNGC
            // Type verbatim here (per spec 9.2's literal field list) is correct as-is.
            ObjectType: row.Type,
            Aliases: [rowName]);
    }

    // Port of openngc.enrich_target_from_openngc. Applied to a target in memory before any
    // match/insert decision (spec 9.7 step 2) and, later phases, by the "rebuild targets"
    // maintenance action. Fills only null fields; never overwrites a value the target (or an
    // earlier enrichment step, e.g. Task 7's SAC enrichment) already set. Returns true if any
    // field changed.
    public static bool EnrichFromOpenNgc(GalactiLogContext context, Entities.Target target)
    {
        if (target.UserDefined)
        {
            return false;
        }

        var entry = LookupOpenNgcForEnrichment(context, target.CatalogId);
        if (entry is null)
        {
            return false;
        }

        var updated = false;
        if (entry.Constellation is not null && target.Constellation is null) { target.Constellation = entry.Constellation; updated = true; }
        if (entry.MajorAxis is not null && target.SizeMajor is null) { target.SizeMajor = entry.MajorAxis; updated = true; }
        if (entry.MinorAxis is not null && target.SizeMinor is null) { target.SizeMinor = entry.MinorAxis; updated = true; }
        if (entry.PositionAngle is not null && target.PositionAngle is null) { target.PositionAngle = entry.PositionAngle; updated = true; }
        if (entry.VMag is not null && target.VMag is null) { target.VMag = entry.VMag; updated = true; }
        if (entry.SurfaceBrightness is not null && target.SurfaceBrightness is null) { target.SurfaceBrightness = entry.SurfaceBrightness; updated = true; }

        if (target.CommonName is null && entry.CommonNames is not null)
        {
            var ngcCommon = OpenNgcCatalog.ExtractCommonName(entry.CommonNames);
            if (ngcCommon is not null)
            {
                target.CommonName = ngcCommon;
                // primary_name is a display label; catalog_id (and the normalized identity
                // computed from it) is untouched, so this rewrite is always safe here.
                target.PrimaryName = AliasCurator.BuildPrimaryName(target.CatalogId, ngcCommon);
                updated = true;
            }
        }

        return updated;
    }

    private static readonly Regex MessierLookupPattern = new(@"^M\s*(\d+)$", RegexOptions.IgnoreCase);

    // Port of openngc.lookup_openngc: try the normalized NGC/IC name first (a wasted query
    // for a Messier-only catalog_id like "M 31", but this is a literal, deliberate port --
    // see the Python source), then the zero-padded Messier key. `internal`, not `private`:
    // Task 7's CatalogMembershipMatcher reuses this exact lookup (same assembly) to find the
    // OpenNGC row for a target's Messier membership regardless of whether the target's
    // CatalogId is already an "M n" form or an "NGC n"/"IC n" form -- the second occurrence
    // of this exact lookup, so it is shared rather than re-implemented (design-lessons rule 1).
    internal static Entities.OpenNgcCatalogEntry? LookupOpenNgcForEnrichment(GalactiLogContext context, string? catalogId)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return null;
        }
        var normalized = NameNormalizer.NormalizeNgcName(catalogId);
        var byName = context.OpenNgcCatalogEntries.SingleOrDefault(e => e.Name == normalized);
        if (byName is not null)
        {
            return byName;
        }

        var m = MessierLookupPattern.Match(catalogId.Trim());
        if (!m.Success)
        {
            return null;
        }
        var key = $"M {m.Groups[1].Value.PadLeft(3, '0')}";
        return context.OpenNgcCatalogEntries.FirstOrDefault(e => e.Messier == key);
    }
}
