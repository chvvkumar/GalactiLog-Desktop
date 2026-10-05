using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GalactiLog.Data.Repositories;

/// <summary>The outcome of <see cref="MergeRepository.Merge"/>. Every failure is an outcome
/// rather than an exception, matching <see cref="RenameOutcome"/>, so the dialog reports it as a
/// message instead of failing on the UI thread.</summary>
public enum MergeStatus
{
    Merged,

    /// <summary>No <c>targets</c> row with that id, or the row is itself merged away. A
    /// merged-away target cannot absorb another.</summary>
    WinnerNotFound,

    LoserNotFound,
    SameTarget,

    /// <summary>The loser is already merged away. Nothing was written.</summary>
    AlreadyMerged,
}

/// <summary>What a merge did, for spec 12.9's "Confirm ... reports the counts".</summary>
public sealed record MergeResult(
    MergeStatus Status,
    int FramesMoved,
    int NotesRekeyed,
    int NotesAppended,
    IReadOnlyList<string> AliasesAdded,
    Guid? ManifestId);

public enum UnmergeStatus
{
    Unmerged,

    /// <summary>No <c>targets</c> row with that id.</summary>
    NotFound,

    /// <summary>The target is not merged away. A second unmerge lands here, which is spec 18.1's
    /// "a second unmerge is a no-op".</summary>
    NotMerged,

    /// <summary>Merged away, but no manifest records it. Spec 9.7: there is no fallback path, so
    /// nothing is restored and nothing is changed.</summary>
    NoManifest,

    /// <summary>The manifest exists but its payload does not parse. Nothing is restored and the
    /// manifest is kept: a manifest that cannot be read cannot be proved consumed.</summary>
    ManifestUnreadable,

    /// <summary>This manifest's winner has itself been merged away since (B into A, then A into
    /// C). Undoing out of order would hand the loser back frames that now live on the later
    /// winner, so nothing is written: the later merge has to be undone first. Undo is exact only
    /// in reverse order (F17).</summary>
    WinnerMergedAway,
}

/// <param name="WinnerMergedIntoName">Only set with
/// <see cref="UnmergeStatus.WinnerMergedAway"/>: the <c>primary_name</c> of the target this
/// manifest's winner was merged into, so the caller can name the merge that has to be undone
/// first.</param>
public sealed record UnmergeResult(
    UnmergeStatus Status,
    int FramesRestored,
    int NotesRestored,
    IReadOnlyList<string> AliasesRemoved,
    string? WinnerMergedIntoName = null);

/// <summary>
/// Spec 9.7's merge and unmerge. Database rows only: nothing here touches the filesystem, and a
/// loser <c>targets</c> row is never deleted, only marked with <c>merged_into_id</c> and
/// <c>merged_at</c>.
/// </summary>
/// <remarks>
/// <para>
/// Takes the DI <see cref="DatabaseConnectionString"/> and opens its own short-lived tracking
/// context per call, matching <c>TargetWriteRepository</c>, <c>ScanRunRepository</c> and
/// <c>ActivityRepository</c>. Each public method runs inside one explicit
/// <c>context.Database.BeginTransaction()</c>: a merge touches five tables and a half-applied
/// merge has no manifest that describes it.
/// </para>
/// <para>
/// One of three App-layer writers of <c>targets</c> rows (questions.md Q20): this type,
/// <c>TargetWriteRepository</c> and <c>TargetEnrichmentRepository</c>. <c>TargetResolver</c>
/// writes them too, but no App surface calls it directly: the scan and spec 9.7's unresolved-name
/// retry drive it. This is the only writer of <c>merged_into_id</c> and <c>merged_at</c>. A merge
/// is one transaction over five tables, where <c>TargetWriteRepository</c>'s three methods are
/// each one row on one table, so it is a sibling of that type rather than an extension of it.
/// </para>
/// </remarks>
public sealed class MergeRepository(DatabaseConnectionString connectionString)
{
    private const string Pending = "pending";
    private const string Accepted = "accepted";

    /// <summary>Spec 5.10's <c>method</c> value meaning no target was suggested, which is the one
    /// case where an unresolved-name merge writes <c>suggested_target_id</c> itself.</summary>
    private const string OrphanMethod = "orphan";

    /// <summary>Merges an active loser target into an active winner target.</summary>
    public MergeResult Merge(Guid winnerId, Guid loserId)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var winner = context.Targets.SingleOrDefault(row => row.Id == winnerId);
        if (winner is null || winner.MergedIntoId is not null)
        {
            return NoMerge(MergeStatus.WinnerNotFound);
        }

        var loser = context.Targets.SingleOrDefault(row => row.Id == loserId);
        if (loser is null)
        {
            return NoMerge(MergeStatus.LoserNotFound);
        }

        if (loserId == winnerId)
        {
            return NoMerge(MergeStatus.SameTarget);
        }

        if (loser.MergedIntoId is not null)
        {
            return NoMerge(MergeStatus.AlreadyMerged);
        }

        var now = DateTime.UtcNow;

        // Spec 9.7's "records a manifest before moving anything": everything the manifest
        // records is read and decided before any write. Statement order inside the transaction is
        // not the guarantee (the ExecuteUpdate below issues before SaveChanges flushes the
        // insert); the transaction's atomicity is, so no committed state has moved frames without
        // a manifest describing them.
        var movedImageIds = context.Images
            .Where(image => image.ResolvedTargetId == loserId)
            .OrderBy(image => image.Id)
            .Select(image => image.Id)
            .ToList();

        // Ruling C3: the sixth table. The loser's values move to the winner wherever the winner
        // holds no value in the same slot, and the moved ids go into the manifest so the unmerge
        // restores exactly those. Re-keyed on tracked entities, inside this same transaction.
        var movedValueIds = CustomColumnRepository.MoveValuesOnMerge(context, winnerId, loserId);

        var winnerNotesByDate = context.SessionNotes
            .Where(note => note.TargetId == winnerId)
            .ToList()
            .ToDictionary(note => note.SessionDate);

        var rekeys = new List<SessionNote>();
        var appends = new List<(SessionNote WinnerNote, string MergedText)>();
        foreach (var loserNote in context.SessionNotes
            .Where(note => note.TargetId == loserId)
            .OrderBy(note => note.SessionDate)
            .ToList())
        {
            if (winnerNotesByDate.TryGetValue(loserNote.SessionDate, out var winnerNote))
            {
                // The exact form of spec 9.7, three dashes and one space either side of the name.
                // The loser's own row is left in place and keyed to the loser, which is what makes
                // it reappear after an unmerge.
                appends.Add((winnerNote, $"{winnerNote.Notes}\n\n--- merged from {loser.PrimaryName} ---\n{loserNote.Notes}"));
            }
            else
            {
                rekeys.Add(loserNote);
            }
        }

        var notesAppended = appends.Select(entry => new AppendedNote(entry.WinnerNote.Id, entry.WinnerNote.Notes)).ToList();

        var loserNames = NamesOf(loser);
        var (aliases, aliasesAdded) = AbsorbAliases(winner, loserNames);

        var manifest = new MergeManifest
        {
            Id = Guid.NewGuid(),
            WinnerId = winnerId,
            LoserId = loserId,
            Payload = JsonSerializer.Serialize(new MergeManifestPayload(
                movedImageIds,
                rekeys.Select(note => note.Id).ToList(),
                notesAppended,
                aliasesAdded,
                SourceName: null,
                CustomValuesMoved: movedValueIds)),
            CreatedAt = now,
        };
        context.MergeManifests.Add(manifest);

        foreach (var note in rekeys)
        {
            // A re-keyed note keeps its own updated_at, because its text did not change.
            note.TargetId = winnerId;
        }

        foreach (var (winnerNote, mergedText) in appends)
        {
            winnerNote.Notes = mergedText;
            winnerNote.UpdatedAt = now;
        }

        winner.Aliases = JsonSerializer.Serialize(aliases);

        // The recorded id set drives the UPDATE, in both merge shapes, so the rows that move are
        // exactly the rows the manifest says moved and unmerge can undo them one for one.
        var framesMoved = movedImageIds.Count == 0
            ? 0
            : context.Images
                .Where(image => movedImageIds.Contains(image.Id))
                .ExecuteUpdate(setters => setters.SetProperty(image => image.ResolvedTargetId, (Guid?)winnerId));

        loser.MergedIntoId = winnerId;
        loser.MergedAt = now;

        foreach (var candidate in PendingCandidatesFor(context, winnerId, loserId))
        {
            if (candidate.SuggestedTargetId == loserId || Matches(loserNames, candidate.SourceName))
            {
                candidate.Status = Accepted;
                candidate.ResolvedAt = now;
            }
        }

        context.SaveChanges();
        transaction.Commit();

        return new MergeResult(MergeStatus.Merged, framesMoved, rekeys.Count, appends.Count, aliasesAdded, manifest.Id);
    }

    /// <summary>Spec 9.7's second merge shape, the port of <c>merge_targets</c>'s
    /// <c>loser_name</c> branch: an unresolved <c>OBJECT</c> string is added to the winner's
    /// aliases and its unresolved LIGHT frames are assigned to the winner. Writes a manifest with
    /// a null <c>loser_id</c> so the assignment is undoable from the same history list
    /// (questions.md Q8).</summary>
    public MergeResult MergeUnresolvedName(Guid winnerId, string loserName)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var winner = context.Targets.SingleOrDefault(row => row.Id == winnerId);
        if (winner is null || winner.MergedIntoId is not null)
        {
            return NoMerge(MergeStatus.WinnerNotFound);
        }

        var now = DateTime.UtcNow;
        var movedImageIds = UnresolvedFrameIds(context, loserName);
        var (aliases, aliasesAdded) = AbsorbAliases(winner, [loserName]);

        var manifest = new MergeManifest
        {
            Id = Guid.NewGuid(),
            WinnerId = winnerId,
            LoserId = null,
            Payload = JsonSerializer.Serialize(new MergeManifestPayload(movedImageIds, [], [], aliasesAdded, loserName, CustomValuesMoved: [])),
            CreatedAt = now,
        };
        context.MergeManifests.Add(manifest);

        winner.Aliases = JsonSerializer.Serialize(aliases);

        var framesMoved = movedImageIds.Count == 0
            ? 0
            : context.Images
                .Where(image => movedImageIds.Contains(image.Id))
                .ExecuteUpdate(setters => setters.SetProperty(image => image.ResolvedTargetId, (Guid?)winnerId));

        // Both branches are in merge_targets: a candidate already pointing at the winner, and an
        // orphan candidate, which additionally gets the winner so the history row can name it.
        foreach (var candidate in context.MergeCandidates
            .Where(row => row.Status == Pending && (row.SuggestedTargetId == winnerId || row.SuggestedTargetId == null))
            .ToList())
        {
            if (!candidate.SourceName.Equals(loserName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidate.Status = Accepted;
            candidate.ResolvedAt = now;
            candidate.SuggestedTargetId = winnerId;
        }

        context.SaveChanges();
        transaction.Commit();

        // Merged even when the name matched zero frames: the alias was still added, and reporting
        // FramesMoved = 0 is honest.
        return new MergeResult(MergeStatus.Merged, framesMoved, 0, 0, aliasesAdded, manifest.Id);
    }

    /// <summary>Reverses the merge that made <paramref name="loserId"/> merged away, using the
    /// manifest and nothing else, then deletes the manifest.</summary>
    public UnmergeResult Unmerge(Guid loserId)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var loser = context.Targets.SingleOrDefault(row => row.Id == loserId);
        if (loser is null)
        {
            return NoUnmerge(UnmergeStatus.NotFound);
        }

        if (loser.MergedIntoId is null)
        {
            // Exactly what a second unmerge hits, and it writes nothing.
            return NoUnmerge(UnmergeStatus.NotMerged);
        }

        var winnerId = loser.MergedIntoId.Value;
        var winner = context.Targets.SingleOrDefault(row => row.Id == winnerId);
        if (winner is null)
        {
            // Without the winner there is no pair to look up, and nothing is written.
            return NoUnmerge(UnmergeStatus.NoManifest);
        }

        if (WinnerMergedAway(context, winner) is { } mergedInto)
        {
            // F17: B was merged into A and A then into C. B's frames are on C now, so restoring
            // them "back to B" from this manifest would take frames off C that this merge never
            // moved, and A's alias list no longer holds what this manifest says it added. Undo is
            // exact only in reverse order, so this one is refused and nothing is written.
            return NoUnmerge(UnmergeStatus.WinnerMergedAway, mergedInto);
        }

        var manifest = context.MergeManifests
            .Where(row => row.WinnerId == winnerId && row.LoserId == loserId)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .FirstOrDefault();
        if (manifest is null)
        {
            // Spec 9.7 forbids a fallback path, so nothing is restored and nothing is written.
            return NoUnmerge(UnmergeStatus.NoManifest);
        }

        var payload = Deserialize(manifest.Payload);
        if (payload is null)
        {
            return NoUnmerge(UnmergeStatus.ManifestUnreadable);
        }

        var now = DateTime.UtcNow;
        var framesRestored = RestoreFrames(context, payload, winnerId, loserId);

        // Ruling C3. The count is deliberately not reported: UnmergeResult gains no member, the
        // figure is shown nowhere, and adding one would move that record's shape for every
        // existing caller.
        _ = CustomColumnRepository.RestoreValuesOnUnmerge(context, payload.CustomValuesMoved ?? [], loserId);

        var rekeyed = payload.NotesRekeyed?.ToList() ?? [];
        var notesRestored = 0;
        if (rekeyed.Count > 0)
        {
            foreach (var note in context.SessionNotes.Where(row => rekeyed.Contains(row.Id)).ToList())
            {
                note.TargetId = loserId;
                notesRestored++;
            }
        }

        foreach (var entry in payload.NotesAppended ?? [])
        {
            var note = context.SessionNotes.SingleOrDefault(row => row.Id == entry.NoteId);
            if (note is null)
            {
                continue;
            }

            note.Notes = entry.PrevText;
            note.UpdatedAt = now;
            notesRestored++;
        }

        var aliasesRemoved = RemoveAddedAliases(winner, payload);

        loser.MergedIntoId = null;
        loser.MergedAt = null;

        var loserNames = NamesOf(loser);
        foreach (var candidate in AcceptedCandidatesFor(context, winnerId, loserId))
        {
            if (candidate.SuggestedTargetId == loserId || Matches(loserNames, candidate.SourceName))
            {
                candidate.Status = Pending;
                candidate.ResolvedAt = null;
            }
        }

        // Spec 5.11: "Consumed and deleted by unmerge."
        context.MergeManifests.Remove(manifest);

        context.SaveChanges();
        transaction.Commit();

        return new UnmergeResult(UnmergeStatus.Unmerged, framesRestored, notesRestored, aliasesRemoved);
    }

    /// <summary>Reverses an unresolved-name merge by its manifest id, which is what the history
    /// row for a null-<c>loser_id</c> manifest carries.</summary>
    public UnmergeResult UndoUnresolvedNameMerge(Guid manifestId)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var manifest = context.MergeManifests.SingleOrDefault(row => row.Id == manifestId);
        if (manifest is null)
        {
            return NoUnmerge(UnmergeStatus.NoManifest);
        }

        if (manifest.LoserId is not null)
        {
            // A target merge, which the caller undoes through Unmerge.
            return NoUnmerge(UnmergeStatus.NotMerged);
        }

        var payload = Deserialize(manifest.Payload);
        if (payload is null)
        {
            return NoUnmerge(UnmergeStatus.ManifestUnreadable);
        }

        // Read before anything is written (F17): the same reverse-order rule applies here, and a
        // refusal has to leave the database untouched.
        var winner = context.Targets.SingleOrDefault(row => row.Id == manifest.WinnerId);
        if (winner is not null && WinnerMergedAway(context, winner) is { } mergedInto)
        {
            return NoUnmerge(UnmergeStatus.WinnerMergedAway, mergedInto);
        }

        // There are no notes to restore: an unresolved name has no session_notes rows, because
        // session_notes.target_id is not nullable (spec 5.9).
        var framesRestored = RestoreFrames(context, payload, manifest.WinnerId, loserId: null);

        IReadOnlyList<string> aliasesRemoved = winner is null ? [] : RemoveAddedAliases(winner, payload);

        // The manifest's own source_name names the candidate rows, not aliases_added: a merge
        // whose alias the winner already carried adds nothing, and would otherwise leave its
        // candidate rows accepted forever (review finding 5).
        if (payload.SourceName is { } sourceName)
        {
            foreach (var candidate in context.MergeCandidates
                .Where(row => row.Status == Accepted && row.SuggestedTargetId == manifest.WinnerId)
                .ToList())
            {
                if (!candidate.SourceName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidate.Status = Pending;
                candidate.ResolvedAt = null;

                // The merge pointed an orphan at the winner (spec 5.10: method "orphan" means no
                // suggestion existed), so the undo takes the suggestion back off.
                if (candidate.Method == OrphanMethod)
                {
                    candidate.SuggestedTargetId = null;
                }
            }
        }

        context.MergeManifests.Remove(manifest);

        context.SaveChanges();
        transaction.Commit();

        return new UnmergeResult(UnmergeStatus.Unmerged, framesRestored, 0, aliasesRemoved);
    }

    // ---- the pieces the four methods share ---------------------------------------------

    /// <summary>The winner's aliases after absorbing <paramref name="incoming"/>, plus exactly
    /// the strings that were added. Deduplicated case-insensitively (spec 9.7) against the
    /// winner's own <c>primary_name</c> and its existing aliases, order preserved. A malformed
    /// aliases document parses as an empty list (<c>SqlReaders.ParseAliases</c>) and the merge
    /// rebuilds it rather than failing.</summary>
    /// <remarks>Public because it is the single owner of the alias union rule: Task 4's preview
    /// computes its <c>AliasesToAdd</c> from this method rather than re-implementing it, or the
    /// dialog can promise an alias the merge does not add.</remarks>
    public static (List<string> Aliases, List<string> Added) AbsorbAliases(Target winner, IReadOnlyList<string> incoming)
    {
        var aliases = SqlReaders.ParseAliases(winner.Aliases).ToList();
        var added = new List<string>();

        foreach (var candidate in incoming)
        {
            if (candidate.Equals(winner.PrimaryName, StringComparison.OrdinalIgnoreCase)
                || aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            aliases.Add(candidate);
            added.Add(candidate);
        }

        return (aliases, added);
    }

    /// <summary>Removes exactly the strings the merge recorded in <c>aliases_added</c> and
    /// returns them (ruling Q9). The removal set is never derived from the loser's current names:
    /// that would strip an alias the winner carried before the merge.</summary>
    private static IReadOnlyList<string> RemoveAddedAliases(Target winner, MergeManifestPayload payload)
    {
        var addedAliases = payload.AliasesAdded ?? [];
        if (addedAliases.Count == 0)
        {
            return [];
        }

        var aliases = SqlReaders.ParseAliases(winner.Aliases).ToList();
        var removed = aliases.Where(alias => Matches(addedAliases, alias)).ToList();
        if (removed.Count == 0)
        {
            return [];
        }

        aliases.RemoveAll(alias => Matches(addedAliases, alias));
        winner.Aliases = JsonSerializer.Serialize(aliases);
        return removed;
    }

    /// <summary>Sends the manifest's frames back, but only those still sitting on the winner: a
    /// frame the user has since re-assigned elsewhere is left alone.</summary>
    private static int RestoreFrames(GalactiLogContext context, MergeManifestPayload payload, Guid winnerId, Guid? loserId)
    {
        var movedImageIds = payload.MovedImageIds?.ToList() ?? [];
        if (movedImageIds.Count == 0)
        {
            return 0;
        }

        return context.Images
            .Where(image => movedImageIds.Contains(image.Id) && image.ResolvedTargetId == winnerId)
            .ExecuteUpdate(setters => setters.SetProperty(image => image.ResolvedTargetId, loserId));
    }

    /// <summary>The unresolved LIGHT frames of one <c>OBJECT</c> string, selected through
    /// <c>SqlFragments.GroupKeyExpression</c> so an unquoted numeric OBJECT card still matches
    /// (Phase 5 fix F1), and ordered by id so the manifest is byte-stable for a given
    /// merge.</summary>
    private static List<Guid> UnresolvedFrameIds(GalactiLogContext context, string loserName)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
        command.CommandText = $"""
            SELECT i.id FROM images i
            WHERE i.resolved_target_id IS NULL
              AND i.{SqlFragments.LightFrameOnly}
              AND {SqlFragments.GroupKeyExpression} = @key
            ORDER BY i.id
            """;
        command.Parameters.Add(new SqliteParameter("@key", SqlFragments.UnresolvedKeyPrefix + loserName));

        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static MergeManifestPayload? Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<MergeManifestPayload>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A target's full name set: <c>primary_name</c> plus every alias.</summary>
    private static List<string> NamesOf(Target target)
        => [target.PrimaryName, .. SqlReaders.ParseAliases(target.Aliases)];

    private static bool Matches(IReadOnlyList<string> names, string candidate)
        => names.Contains(candidate, StringComparer.OrdinalIgnoreCase);

    private static List<MergeCandidate> PendingCandidatesFor(GalactiLogContext context, Guid winnerId, Guid loserId)
        => context.MergeCandidates
            .Where(row => row.Status == Pending && (row.SuggestedTargetId == winnerId || row.SuggestedTargetId == loserId))
            .ToList();

    private static List<MergeCandidate> AcceptedCandidatesFor(GalactiLogContext context, Guid winnerId, Guid loserId)
        => context.MergeCandidates
            .Where(row => row.Status == Accepted && (row.SuggestedTargetId == winnerId || row.SuggestedTargetId == loserId))
            .ToList();

    private static MergeResult NoMerge(MergeStatus status) => new(status, 0, 0, 0, [], null);

    private static UnmergeResult NoUnmerge(UnmergeStatus status, string? winnerMergedIntoName = null)
        => new(status, 0, 0, [], winnerMergedIntoName);

    /// <summary>The <c>primary_name</c> of the target <paramref name="winner"/> was itself merged
    /// into, or null when it is still active. A winner whose own winner row is gone counts as
    /// merged away too, under the name "another target": the pointer is what makes the undo
    /// inexact, not the row it points at.</summary>
    private static string? WinnerMergedAway(GalactiLogContext context, Target winner)
    {
        if (winner.MergedIntoId is not { } id)
        {
            return null;
        }

        return context.Targets.SingleOrDefault(row => row.Id == id)?.PrimaryName ?? "another target";
    }

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
}
