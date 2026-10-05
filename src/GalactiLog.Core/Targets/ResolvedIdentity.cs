namespace GalactiLog.Core.Targets;

// A curated identity, produced by the offline lookup (Phase 3 Task 4), SIMBAD curation, or
// SESAME curation (Task 5) alike -- one shape so TargetResolver's downstream steps (identity
// matching 9.5, creation 9.7) run once regardless of which source produced it.
public sealed record ResolvedIdentity(
    string PrimaryName,
    string? CatalogId,
    string? CommonName,
    double? Ra,
    double? Dec,
    string? ObjectType,
    IReadOnlyList<string> Aliases);
