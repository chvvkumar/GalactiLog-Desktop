using System.Text.Json;

namespace GalactiLog.Data.Repositories;

// Thin, per-call-context repository over `targets` (design-spec 9.5). Not a "short-lived
// context per method" repository like SettingsRepository: TargetResolver owns one
// GalactiLogContext for its entire Resolve() call and constructs one TargetRepository against
// it, so a target read in step 2 and a target write in step 8 see the same tracked entities
// within one transaction.
public sealed class TargetRepository(GalactiLogContext context)
{
    public Entities.Target? FindByCatalogIdNormalized(string catalogIdNormalized)
        => context.Targets.SingleOrDefault(t => t.MergedIntoId == null && t.CatalogIdNormalized == catalogIdNormalized);

    // Case-sensitive, exact-string containment -- deliberately NOT case-insensitive. Ports
    // PostgreSQL's `Target.aliases.any(normalized)` (an ARRAY containment check using plain
    // `=` per element, case-sensitive). The alias this lookup is meant to hit is always the
    // UPPERCASE panel-stripped incoming name appended at match/create time (see
    // MatchTargetByIdentity/CreateOrLinkTarget below) -- a display-case SIMBAD alias like
    // "rho Oph" will NOT be found by this method even for an input that normalizes to
    // "RHO OPH", exactly like the Python/Postgres original. This is intentional: the alias
    // array is a fast-path cache of exact prior inputs, not a general fuzzy matcher; a miss
    // here always falls through to slower-but-correct pipeline steps, it never produces a
    // wrong answer.
    public Entities.Target? FindByAliasExact(string normalizedAlias)
        => ActiveTargets().FirstOrDefault(t => DeserializeAliases(t.Aliases).Any(a => a == normalizedAlias));

    public Entities.Target? FindByPrimaryName(string primaryName)
        => context.Targets.SingleOrDefault(t => t.MergedIntoId == null && t.PrimaryName == primaryName);

    // Case-INSENSITIVE dedup check when deciding whether to append an alias -- ports Python's
    // `if incoming not in [a.upper() for a in target.aliases]`. Different case rule from
    // FindByAliasExact above on purpose; see the Python source cited there.
    public void AddAliasIfMissing(Entities.Target target, string uppercaseAlias)
    {
        var list = DeserializeAliases(target.Aliases);
        if (list.Any(a => a.ToUpperInvariant() == uppercaseAlias))
        {
            return;
        }
        list.Add(uppercaseAlias);
        target.Aliases = JsonSerializer.Serialize(list);
    }

    public void Insert(Entities.Target target) => context.Targets.Add(target);

    // Materializes client-side: aliases are a JSON text column, so containment cannot be
    // expressed in SQL. Spec 5.3 accepts this ("at most a few thousand rows").
    private IEnumerable<Entities.Target> ActiveTargets()
        => context.Targets.Where(t => t.MergedIntoId == null).AsEnumerable();

    private static List<string> DeserializeAliases(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
