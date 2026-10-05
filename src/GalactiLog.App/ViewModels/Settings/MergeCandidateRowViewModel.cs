using System.Globalization;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of spec 12.9's candidate list: the source name, its frame count, the suggested
/// target, the similarity score as a percentage, the method, and the reason text.
/// </summary>
/// <remarks>
/// Formatting only, the shape <c>Dashboard.SearchResultViewModel</c> established. No brush lives
/// on this record: a later per-method colour binds a token in XAML through a converter or a style
/// selector, and a view-model that holds a brush holds an <c>ImmutableSolidColorBrush</c>.
/// </remarks>
public sealed record MergeCandidateRowViewModel(MergeCandidateRow Row)
{
    public Guid Id => Row.Id;

    public string SourceName => Row.SourceName;

    /// <summary>Spec 12.9's frame count. "1 frame" / "N frames", matching
    /// <c>SearchResultViewModel.FrameCountText</c>.</summary>
    public string FrameCountText => Row.SourceImageCount == 1
        ? "1 frame"
        : $"{Row.SourceImageCount.ToString(CultureInfo.InvariantCulture)} frames";

    /// <summary>The suggested winner, or "No suggestion" for an orphan candidate. A candidate
    /// whose suggested target was merged away reads the same way, because the query returns no
    /// name for a winner the dashboard no longer offers.</summary>
    public string SuggestedText => Row.SuggestedTargetName ?? "No suggestion";

    public bool HasSuggestion => Row.SuggestedTargetId is not null;

    /// <summary>Spec 12.9's "similarity score as a percentage". Truncated, not rounded, so it
    /// agrees with the trigram reason text the same row carries
    /// (<c>Name is 87% similar to ...</c>), which <c>DuplicateDetector</c> truncates the same
    /// way.</summary>
    public string ScoreText =>
        $"{((int)(Row.SimilarityScore * 100)).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>Spec 5.10's method verbatim: <c>simbad</c>, <c>trigram</c>, <c>orphan</c> or
    /// <c>duplicate</c> (questions.md Q4). Not mapped to a friendlier label: the reason text is
    /// already the human sentence.</summary>
    public string Method => Row.Method;

    public string ReasonText => Row.ReasonText ?? "";

    public bool HasReason => ReasonText.Length > 0;
}
