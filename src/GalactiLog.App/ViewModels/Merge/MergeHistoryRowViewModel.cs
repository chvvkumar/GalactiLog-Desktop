using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Merge;

/// <summary>
/// One row of spec 12.9's merge history: the loser name, the merge time, the moved frame count,
/// and the undo the list binds. Formatting only; the undo itself is
/// <see cref="MergeHistoryViewModel.UndoCommand"/>, because one row must not own a write.
/// </summary>
/// <remarks>
/// The merge time is a stored instant, unlike a <c>session_date</c>, so it renders through
/// <see cref="SessionTimeFormat"/> plus <see cref="MetricText.Date"/>: the one
/// <c>general.timezone</c> / <c>general.use_24h_time</c> path the session cards already use
/// (collision-map designated owners). Nothing here re-implements either half.
/// </remarks>
public sealed class MergeHistoryRowViewModel
{
    /// <summary>The label a row with no name at all reads. Spec 5.11 allows a manifest with a
    /// null <c>loser_id</c>, and a hand-edited payload can carry no <c>source_name</c>.</summary>
    public const string UnresolvedNameLabel = "an unresolved name";

    public MergeHistoryRowViewModel(MergeHistoryRow row, TimeZoneInfo zone, bool use24Hour)
    {
        Row = row;

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(row.MergedAt, DateTimeKind.Utc),
            zone);

        MergedAtText = MetricText.Date(DateOnly.FromDateTime(local))
            + " "
            + SessionTimeFormat.Format(row.MergedAt, zone, use24Hour);

        FrameCountText = row.MovedFrameCount switch
        {
            < 0 => "unknown frames",
            1 => "1 frame",
            var count => count.ToString("N0", CultureInfo.InvariantCulture) + " frames",
        };
    }

    public MergeHistoryRow Row { get; }

    public Guid ManifestId => Row.ManifestId;

    /// <summary>Null for the unresolved-name shape, which is what routes the undo to
    /// <c>MergeRepository.UndoUnresolvedNameMerge</c> instead of <c>Unmerge</c>.</summary>
    public Guid? LoserId => Row.LoserId;

    /// <summary>The merged-away side's display name.</summary>
    public string LoserName => Row.LoserName ?? UnresolvedNameLabel;

    /// <summary>The surviving side's display name.</summary>
    public string WinnerName => Row.WinnerName;

    public string MergedAtText { get; }

    public string FrameCountText { get; }
}
