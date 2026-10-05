namespace GalactiLog.Data.Repositories;

/// <summary>
/// The status writes on <c>merge_candidates</c> that are not part of a merge. A merge's own
/// status transitions belong to <see cref="MergeRepository"/>, which performs them inside the
/// merge transaction (spec 9.7); this type covers only the user's explicit dismiss and the
/// re-pointing of a suggestion.
/// </summary>
/// <remarks>
/// Takes the DI <see cref="DatabaseConnectionString"/> and opens its own short-lived tracking
/// context per call, matching <see cref="TargetWriteRepository"/> and
/// <see cref="MergeRepository"/>. Each method writes at most one row, so neither needs an
/// explicit transaction: <c>SaveChanges</c> is already one.
/// </remarks>
public sealed class MergeCandidateRepository(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// Spec 12.9: dismiss "sets status <c>dismissed</c>". Stamps <c>resolved_at</c>.
    /// </summary>
    /// <returns>False when no pending row with that id exists, so a double click on a row the
    /// list has already dropped is a no-op rather than an exception.</returns>
    public bool Dismiss(Guid candidateId)
    {
        using var context = Open();
        var candidate = context.MergeCandidates
            .SingleOrDefault(row => row.Id == candidateId && row.Status == "pending");
        if (candidate is null)
        {
            return false;
        }

        candidate.Status = "dismissed";
        candidate.ResolvedAt = DateTime.UtcNow;
        context.SaveChanges();
        return true;
    }

    /// <summary>
    /// Spec 12.9's "edit target": re-points a pending candidate at a different winner. The row
    /// stays pending; only the suggestion changes (questions.md Q17).
    /// </summary>
    /// <returns>False when no pending row with that id exists.</returns>
    public bool Retarget(Guid candidateId, Guid suggestedTargetId)
    {
        using var context = Open();
        var candidate = context.MergeCandidates
            .SingleOrDefault(row => row.Id == candidateId && row.Status == "pending");
        if (candidate is null)
        {
            return false;
        }

        candidate.SuggestedTargetId = suggestedTargetId;
        context.SaveChanges();
        return true;
    }

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
}
