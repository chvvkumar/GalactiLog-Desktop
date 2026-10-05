using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>What the card factory needs beyond the overview to build a merged card (P25 R2);
/// null builds a ledger card.</summary>
/// <param name="GetDetail">The merging reader the card loads through, in place of the session
/// detail query.</param>
/// <param name="Nights">The member nights, oldest first.</param>
/// <param name="NoteNights">The member cards in the same order, whose notes fields the merged
/// card's notes section shows.</param>
public sealed record MergedCardSpec(
    Func<string, DateOnly, SessionDetail?> GetDetail,
    IReadOnlyList<DateOnly> Nights,
    IReadOnlyList<SessionCardViewModel> NoteNights);
