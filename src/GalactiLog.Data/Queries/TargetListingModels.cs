namespace GalactiLog.Data.Queries;

/// <summary>One filter badge of the Palette column: a canonical filter name, its configured
/// colour, and what that filter contributed to the group. Frames with a null or empty
/// <c>filter_used</c> fold to the canonical name "Unknown" with the default colour, so the
/// badge counts always sum to the group's frame count.</summary>
public sealed record FilterBadge(string CanonicalName, string Color, int FrameCount, double IntegrationSeconds);

/// <summary>One row of a target's Sessions expander. <paramref name="Filters"/> is a trailing
/// optional positional member (TRACKING item 37) so every existing <c>new SessionSummary(</c>
/// call site keeps compiling; it reuses <see cref="FilterBadge"/> rather than a second badge
/// type. Spec 12.2's Filters column: the canonical filter names present in that night's LIGHT
/// frames, deduplicated, in the order the Filters tab stores them. Unlike the Palette column
/// (<c>Q14</c>'s Unknown fold), a frame with a null or empty <c>filter_used</c> contributes no
/// name at all here, so a night whose every frame lacks one carries an empty list rather than an
/// "Unknown" badge.</summary>
public sealed record SessionSummary(
    DateOnly SessionDate,
    int FrameCount,
    double IntegrationSeconds,
    IReadOnlyList<FilterBadge>? Filters = null)
{
    public IReadOnlyList<FilterBadge> Filters { get; init; } = Filters ?? [];
}

/// <summary>One row of the dashboard target list (spec 12.2 "Target list columns"). A read
/// model: entities never leave GalactiLog.Data.Queries. <see cref="Mosaics"/> is spec 12.2's
/// mosaic link set (Phase 18): the mosaics with an <c>included</c> row of this target, in ordinal
/// case-insensitive name order, empty for an unresolved group; an init member so every existing
/// <c>new TargetRow(</c> site keeps compiling.</summary>
public sealed record TargetRow(
    string GroupKey,
    Guid? TargetId,
    string Name,
    string? CommonName,
    string? CatalogId,
    string? ObjectType,
    string ObjectCategory,
    double IntegrationSeconds,
    int FrameCount,
    int SessionCount,
    DateOnly? FirstSession,
    DateOnly? LastSession,
    IReadOnlyList<FilterBadge> Palette,
    IReadOnlyList<string> Equipment,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<SessionSummary> Sessions)
{
    public IReadOnlyList<MosaicLink> Mosaics { get; init; } = [];
}

/// <summary>One page of the target list plus the aggregates over the whole filtered set, which
/// feed the summary strip. <see cref="Page"/> and <see cref="PageSize"/> are the clamped values
/// the query actually used.</summary>
public sealed record TargetListingPage(
    IReadOnlyList<TargetRow> Rows,
    int TotalGroups,
    double TotalIntegrationSeconds,
    int TotalFrames,
    int Page,
    int PageSize);
