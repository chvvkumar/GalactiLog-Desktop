namespace GalactiLog.Core.Aliases;

/// <summary>
/// The one place the spec 5.8.4 filter-colour fallback lives (FIXER LIST F10). A filter's tint is
/// user data, not a theme token, so it is the single colour value the application reads from
/// settings; a malformed one falls back here rather than throwing in a view-model.
/// </summary>
/// <remarks>
/// This type deliberately holds the rule and not the brush: <c>GalactiLog.Core</c> must not
/// reference Avalonia (<c>CoreHasNoUiOrEfReferenceTest</c>), so the brush construction stays in
/// the App layer, which calls <see cref="OrFallback"/> for the value.
/// </remarks>
public static class FilterColor
{
    /// <summary>Spec 5.8.4's default filter colour.</summary>
    public const string Fallback = "#808080";

    /// <summary>The configured colour, trimmed, or <see cref="Fallback"/> when it is absent or
    /// blank. A syntactically invalid value is returned as given; the caller decides what its
    /// colour parser makes of it and falls back again if it cannot.</summary>
    public static string OrFallback(string? color)
        => string.IsNullOrWhiteSpace(color) ? Fallback : color.Trim();

    /// <summary>
    /// A stored colour that carries the user's intent, trimmed, or null when it carries none
    /// (P13 review P3-5, coordinator ruling). Blank is none. <see cref="Fallback"/> itself is
    /// none: every build before Phase 13 wrote this exact string into every group it created,
    /// without the user asking, so a document holding it records what an old default was and not
    /// what anybody chose.
    /// </summary>
    /// <remarks>
    /// The consequence, which spec 5.8.4 records: a user who wants a filter drawn grey picks any
    /// grey but <c>#808080</c>. <c>#7f7f7f</c> is kept, as is every other value.
    /// </remarks>
    public static string? AsStored(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var trimmed = color.Trim();
        return string.Equals(trimmed, Fallback, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    /// <summary>
    /// The one filter-colour resolution in the application (HANDOFF 5.2 item 12, P13 R2). In
    /// order: the stored colour, when it carries intent; the seeded palette entry for the category
    /// <paramref name="canonical"/> folds to; the seeded palette entry for the category the first
    /// of <paramref name="aliases"/> folds to, in the order given; <see cref="Fallback"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both callers go through this and neither carries a second copy of the order:
    /// <c>AliasMap.FilterColor</c>, which every consuming surface reaches, and
    /// <c>AliasGroupViewModel.ResolvedColor</c>, which is the Settings swatch and the picker
    /// (review P2-1: a three-step copy there made the Filters tab disagree with the ledger dots
    /// for a group whose alias, not its canonical name, carried the category).
    /// </para>
    /// <para>
    /// The web's <c>getFilterColor</c> has a second step this order does not, "the stored colour
    /// for the raw name". It has no port analogue: the canonical name is what both callers hold,
    /// because <c>AliasMap.CanonicalFilter</c> is what folds a raw name first. The alias sweep
    /// covers the same ground from the other side (ruling Q3). The terminal grey stays spec
    /// 5.8.4's <c>#808080</c> and not the web's <c>#666666</c> (ruling Q1).
    /// </para>
    /// </remarks>
    public static string Resolve(string? stored, string? canonical, IEnumerable<string>? aliases)
    {
        if (AsStored(stored) is { } chosen)
        {
            return chosen;
        }

        return FilterCategory.Of(canonical, aliases) is { } category
            ? FilterCategory.Defaults[category]
            : Fallback;
    }
}
