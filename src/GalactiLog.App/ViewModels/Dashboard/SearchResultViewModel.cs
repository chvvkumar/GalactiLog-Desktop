using GalactiLog.Core.Targets;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One row of the dashboard search dropdown (spec 12.2): the display name, the object type
/// category, and the matched alias when it is not simply the name already shown. An unresolved
/// <c>OBJECT</c> row carries its frame count instead of a type.
/// </summary>
/// <remarks>
/// Formatting only. The ranking and the threshold belong to <see cref="TargetSearchQuery"/>; this
/// type never reorders or filters what the query returned, which is what the App-level tests
/// assert (coordinator ruling Q8). Phase 7's merge dialog needs the same search box (spec 12.9)
/// and reuses this type as-is.
/// </remarks>
public sealed record SearchResultViewModel(TargetSearchResult Result)
{
    public Guid? TargetId => Result.TargetId;

    public string? UnresolvedObject => Result.UnresolvedObject;

    public string DisplayName => Result.DisplayName;

    public bool IsUnresolved => Result.TargetId is null;

    /// <summary>The spec 9.8 display category of the raw OTYPELIST, or
    /// <see cref="TargetListingCriteria.UnresolvedCategory"/> for an <c>obj:</c> row, so the
    /// dropdown and the Object Type pills speak the same vocabulary.</summary>
    public string CategoryText => IsUnresolved
        ? TargetListingCriteria.UnresolvedCategory
        : ObjectTypeCategories.Categorize(Result.ObjectType);

    /// <summary>Spec 12.2's "the matched alias". Empty when the score came from the name already
    /// on the row, so the dropdown does not repeat itself.</summary>
    public string MatchedText =>
        !string.IsNullOrWhiteSpace(Result.MatchedOn)
        && !string.Equals(Result.MatchedOn, Result.DisplayName, StringComparison.Ordinal)
            ? $"matched {Result.MatchedOn}"
            : "";

    public bool HasMatchedText => MatchedText.Length > 0;

    /// <summary>Spec 12.2: unresolved <c>OBJECT</c> strings "appear with their frame count".</summary>
    public string FrameCountText => Result.FrameCount == 1 ? "1 frame" : $"{Result.FrameCount} frames";

    public bool HasFrameCount => IsUnresolved;
}
