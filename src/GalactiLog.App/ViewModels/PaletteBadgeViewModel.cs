using Avalonia.Media;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels;

/// <summary>One filter badge: the read model plus its parsed colour, so the view binds a brush and
/// holds no conversion logic. Shared by the dashboard's Palette column and session line and the
/// Statistics Filters cell, which all draw it through Theme/Controls.axaml's FilterBadgeTemplate.
/// </summary>
/// <remarks>FIXER LIST F29, widened: <see cref="Tint"/> is only ever
/// <c>TargetRowViewModel.ParseTint</c>'s result, so the type says immutable rather than leaving
/// spec 14.5's rule to convention, exactly as <c>FilterUsageRow.Tint</c> does. The chip's wash,
/// edge and shadow are derived from it by <c>Controls.FilterChip</c>.</remarks>
public sealed record PaletteBadgeViewModel(FilterBadge Badge, IImmutableSolidColorBrush Tint)
{
    public string CanonicalName => Badge.CanonicalName;
    public int FrameCount => Badge.FrameCount;

    /// <summary>The badge's tooltip since Phase 14C (spec 12.2, user ruling U3): the badge draws
    /// <see cref="CanonicalName"/> alone, so the figure that used to sit inside it and wrap the
    /// cell to a second line reads on hover instead. Singular at one frame.
    /// <para>
    /// <see cref="FrameCount"/> stays public beside it: showing the figure in the badge again is a
    /// one line template change rather than a view-model change (Q1, provisional).
    /// </para></summary>
    public string FrameCountText
        => FrameCount == 1 ? $"{CanonicalName}, 1 frame" : $"{CanonicalName}, {FrameCount} frames";
}
