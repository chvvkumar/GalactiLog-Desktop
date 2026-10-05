using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Phase 7 Task 2. Spec 9.7's merge and unmerge against a migrated temp database: the partial
// unique indexes on targets, the unique index on (target_id, session_date) and the json_extract
// group key are all real here, and none of them can be asserted against an in-memory fake.
public class MergeRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly MergeRepository _repository;

    public MergeRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _repository = new MergeRepository(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    // ---- helpers -----------------------------------------------------------------------

    private static DateOnly Date(int day) => new(2025, 1, day);

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private Target NewTarget(string primaryName, params string[] aliases)
        => LibrarySeeder.AddTarget(
            _db.ConnectionString,
            primaryName,
            target => target.Aliases = JsonSerializer.Serialize(aliases));

    private Image NewFrame(Guid? targetId, int day = 1, Action<Image>? configure = null)
        => LibrarySeeder.AddFrame(_db.ConnectionString, targetId, Date(day), configure);

    private Image NewUnresolvedFrame(string rawHeaders, int day = 1, string imageType = "LIGHT")
        => LibrarySeeder.AddFrame(_db.ConnectionString, null, Date(day), image =>
        {
            image.ImageType = imageType;
            image.RawHeaders = rawHeaders;
        });

    private SessionNote NewNote(Guid targetId, int day, string notes)
        => LibrarySeeder.AddSessionNote(_db.ConnectionString, targetId, Date(day), notes);

    private MergeCandidate NewCandidate(string sourceName, Guid? suggested, string status = "pending", string method = "orphan")
        => LibrarySeeder.AddMergeCandidate(_db.ConnectionString, sourceName, candidate =>
        {
            candidate.SuggestedTargetId = suggested;
            candidate.Status = status;
            candidate.Method = method;
        });

    private Target ReadTarget(Guid id)
    {
        using var context = Open();
        return context.Targets.Single(row => row.Id == id);
    }

    private static string[] AliasesOf(Target target)
        => JsonSerializer.Deserialize<string[]>(target.Aliases) ?? [];

    private Guid? FrameTargetOf(Guid imageId)
    {
        using var context = Open();
        return context.Images.Single(row => row.Id == imageId).ResolvedTargetId;
    }

    private SessionNote ReadNote(Guid id)
    {
        using var context = Open();
        return context.SessionNotes.Single(row => row.Id == id);
    }

    private MergeCandidate ReadCandidate(Guid id)
    {
        using var context = Open();
        return context.MergeCandidates.Single(row => row.Id == id);
    }

    private List<MergeManifest> Manifests()
    {
        using var context = Open();
        return [.. context.MergeManifests];
    }

    private MergeManifestPayload PayloadOf(Guid manifestId)
    {
        using var context = Open();
        var json = context.MergeManifests.Single(row => row.Id == manifestId).Payload;
        return JsonSerializer.Deserialize<MergeManifestPayload>(json)!;
    }

    /// <summary>Winner, loser, and the loser's frames, which is the shape most of these cases
    /// start from.</summary>
    private (Target Winner, Target Loser, List<Guid> Frames) Pair(int loserFrames = 2)
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224", "Andromeda");
        var frames = new List<Guid>();
        for (var i = 0; i < loserFrames; i++)
        {
            frames.Add(NewFrame(loser.Id, day: i + 1).Id);
        }

        return (winner, loser, frames);
    }

    // ---- merge -------------------------------------------------------------------------

    [Fact]
    public void Merge_WritesAManifestBeforeMovingAnything()
    {
        var (winner, loser, frames) = Pair(loserFrames: 3);

        var result = _repository.Merge(winner.Id, loser.Id);

        // Spec 9.7: the manifest records "the exact set of image ids on the loser", as it was
        // before the move.
        Assert.Equal(MergeStatus.Merged, result.Status);
        var payload = PayloadOf(result.ManifestId!.Value);
        Assert.Equal(frames.OrderBy(id => id).ToList(), payload.MovedImageIds.OrderBy(id => id).ToList());
        Assert.Equal(3, payload.MovedImageIds.Count);
    }

    [Fact]
    public void Merge_MovesEveryLoserFrameToTheWinner()
    {
        var (winner, loser, frames) = Pair(loserFrames: 3);

        _repository.Merge(winner.Id, loser.Id);

        Assert.All(frames, id => Assert.Equal(winner.Id, FrameTargetOf(id)));
    }

    [Fact]
    public void Merge_LeavesTheLoserRowInPlace()
    {
        var (winner, loser, _) = Pair();

        _repository.Merge(winner.Id, loser.Id);

        // Spec 9.7: "the row is never deleted".
        var stored = ReadTarget(loser.Id);
        Assert.Equal("NGC 224", stored.PrimaryName);
    }

    [Fact]
    public void Merge_SetsMergedIntoIdAndMergedAt()
    {
        var (winner, loser, _) = Pair();
        var before = DateTime.UtcNow.AddSeconds(-1);

        _repository.Merge(winner.Id, loser.Id);

        var stored = ReadTarget(loser.Id);
        Assert.Equal(winner.Id, stored.MergedIntoId);
        Assert.NotNull(stored.MergedAt);
        Assert.InRange(stored.MergedAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Merge_AbsorbsTheLoserPrimaryNameIntoTheWinnerAliases()
    {
        var (winner, loser, _) = Pair();

        _repository.Merge(winner.Id, loser.Id);

        Assert.Contains("NGC 224", AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void Merge_AbsorbsEveryLoserAlias()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224", "Andromeda", "Andromeda Galaxy");

        _repository.Merge(winner.Id, loser.Id);

        var aliases = AliasesOf(ReadTarget(winner.Id));
        Assert.Equal(["NGC 224", "Andromeda", "Andromeda Galaxy"], aliases);
    }

    [Fact]
    public void Merge_DeduplicatesAliasesCaseInsensitively()
    {
        var winner = NewTarget("M 31", "andromeda");
        var loser = NewTarget("NGC 224", "Andromeda");

        var result = _repository.Merge(winner.Id, loser.Id);

        // Spec 9.7's "deduplicated case-insensitively": the winner's own spelling stands and the
        // loser's differently-cased copy is not appended.
        Assert.Equal(["andromeda", "NGC 224"], AliasesOf(ReadTarget(winner.Id)));
        Assert.Equal(["NGC 224"], result.AliasesAdded);
    }

    [Fact]
    public void Merge_DoesNotAddTheWinnerOwnNameAsAnAlias()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224", "m 31");

        _repository.Merge(winner.Id, loser.Id);

        Assert.Equal(["NGC 224"], AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void Merge_MalformedWinnerAliasesJson_RebuildsTheArray()
    {
        var winner = LibrarySeeder.AddTarget(_db.ConnectionString, "M 31", target => target.Aliases = "not json");
        var loser = NewTarget("NGC 224", "Andromeda");

        _repository.Merge(winner.Id, loser.Id);

        // ParseAliases semantics: a malformed document yields an empty list and the merge rebuilds
        // it rather than failing.
        Assert.Equal(["NGC 224", "Andromeda"], AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void Merge_NoteOnADateTheWinnerLacks_IsRekeyedToTheWinner()
    {
        var (winner, loser, _) = Pair();
        var note = NewNote(loser.Id, 4, "clear, no moon");

        _repository.Merge(winner.Id, loser.Id);

        var stored = ReadNote(note.Id);
        Assert.Equal(winner.Id, stored.TargetId);
        Assert.Equal("clear, no moon", stored.Notes);

        // A re-keyed note keeps its own updated_at, because its text did not change.
        Assert.Equal(note.UpdatedAt, stored.UpdatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Merge_RekeyedNoteIdIsRecordedInTheManifest()
    {
        var (winner, loser, _) = Pair();
        var note = NewNote(loser.Id, 4, "clear, no moon");

        var result = _repository.Merge(winner.Id, loser.Id);

        Assert.Equal([note.Id], PayloadOf(result.ManifestId!.Value).NotesRekeyed);
    }

    [Fact]
    public void Merge_NoteOnACollidingDate_AppendsWithTheSpecifiedMarker()
    {
        var (winner, loser, _) = Pair();
        var winnerNote = NewNote(winner.Id, 4, "winner text");
        NewNote(loser.Id, 4, "loser text");

        _repository.Merge(winner.Id, loser.Id);

        // The exact form of spec 9.7: three dashes, one space either side of the loser's name,
        // \n and not \r\n.
        Assert.Equal("winner text\n\n--- merged from NGC 224 ---\nloser text", ReadNote(winnerNote.Id).Notes);
    }

    [Fact]
    public void Merge_CollidingNote_LeavesTheLoserRowInPlace()
    {
        var (winner, loser, _) = Pair();
        NewNote(winner.Id, 4, "winner text");
        var loserNote = NewNote(loser.Id, 4, "loser text");

        _repository.Merge(winner.Id, loser.Id);

        // The rule that makes unmerge work: the loser's own row is keyed to the loser and
        // reappears after an unmerge.
        var stored = ReadNote(loserNote.Id);
        Assert.Equal(loser.Id, stored.TargetId);
        Assert.Equal("loser text", stored.Notes);
    }

    [Fact]
    public void Merge_CollidingNote_RecordsThePriorWinnerText()
    {
        var (winner, loser, _) = Pair();
        var winnerNote = NewNote(winner.Id, 4, "winner text");
        NewNote(loser.Id, 4, "loser text");

        var result = _repository.Merge(winner.Id, loser.Id);

        var appended = Assert.Single(PayloadOf(result.ManifestId!.Value).NotesAppended);
        Assert.Equal(winnerNote.Id, appended.NoteId);
        Assert.Equal("winner text", appended.PrevText);
    }

    [Fact]
    public void Merge_AcceptsPendingCandidatesPointingAtTheWinnerWithALoserName()
    {
        var (winner, loser, _) = Pair();
        var named = NewCandidate("andromeda", winner.Id);
        var unrelated = NewCandidate("Sh2-155", winner.Id);

        _repository.Merge(winner.Id, loser.Id);

        var accepted = ReadCandidate(named.Id);
        Assert.Equal("accepted", accepted.Status);
        Assert.NotNull(accepted.ResolvedAt);
        Assert.Equal("pending", ReadCandidate(unrelated.Id).Status);
    }

    [Fact]
    public void Merge_AcceptsPendingCandidatesPointingAtTheLoser()
    {
        var (winner, loser, _) = Pair();
        var candidate = NewCandidate("whatever", loser.Id);

        _repository.Merge(winner.Id, loser.Id);

        Assert.Equal("accepted", ReadCandidate(candidate.Id).Status);
    }

    [Fact]
    public void Merge_SameTarget_ReturnsSameTargetAndWritesNothing()
    {
        var winner = NewTarget("M 31");
        var frame = NewFrame(winner.Id);

        var result = _repository.Merge(winner.Id, winner.Id);

        Assert.Equal(MergeStatus.SameTarget, result.Status);
        Assert.Empty(Manifests());
        Assert.Null(ReadTarget(winner.Id).MergedIntoId);
        Assert.Equal(winner.Id, FrameTargetOf(frame.Id));
    }

    [Fact]
    public void Merge_MergedAwayLoser_ReturnsAlreadyMergedAndWritesNothing()
    {
        var (winner, loser, _) = Pair();
        _repository.Merge(winner.Id, loser.Id);
        var third = NewTarget("M 110");

        var result = _repository.Merge(third.Id, loser.Id);

        Assert.Equal(MergeStatus.AlreadyMerged, result.Status);
        Assert.Single(Manifests());
        Assert.Equal(winner.Id, ReadTarget(loser.Id).MergedIntoId);
        Assert.Empty(AliasesOf(ReadTarget(third.Id)));
    }

    [Fact]
    public void Merge_MergedAwayWinner_ReturnsWinnerNotFoundAndWritesNothing()
    {
        var (winner, loser, _) = Pair();
        _repository.Merge(winner.Id, loser.Id);
        var third = NewTarget("M 110");

        // A merged-away target cannot absorb another.
        var result = _repository.Merge(loser.Id, third.Id);

        Assert.Equal(MergeStatus.WinnerNotFound, result.Status);
        Assert.Single(Manifests());
        Assert.Null(ReadTarget(third.Id).MergedIntoId);
    }

    [Fact]
    public void Merge_UnknownWinner_ReturnsWinnerNotFound()
    {
        var loser = NewTarget("NGC 224");

        var result = _repository.Merge(Guid.NewGuid(), loser.Id);

        Assert.Equal(MergeStatus.WinnerNotFound, result.Status);
        Assert.Empty(Manifests());
    }

    [Fact]
    public void Merge_UnknownLoser_ReturnsLoserNotFound()
    {
        var winner = NewTarget("M 31");

        var result = _repository.Merge(winner.Id, Guid.NewGuid());

        Assert.Equal(MergeStatus.LoserNotFound, result.Status);
        Assert.Empty(Manifests());
    }

    [Fact]
    public void Merge_ReportsTheCounts()
    {
        var (winner, loser, _) = Pair(loserFrames: 3);
        NewNote(winner.Id, 4, "winner text");
        NewNote(loser.Id, 4, "loser text");
        NewNote(loser.Id, 5, "another night");

        var result = _repository.Merge(winner.Id, loser.Id);

        // Spec 12.9: confirm "reports the counts".
        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal(3, result.FramesMoved);
        Assert.Equal(1, result.NotesRekeyed);
        Assert.Equal(1, result.NotesAppended);
        Assert.Equal(["NGC 224", "Andromeda"], result.AliasesAdded);
        Assert.NotNull(result.ManifestId);
    }

    [Fact]
    public void Merge_TouchesNoCalibrationFrameOfAnotherTarget()
    {
        var (winner, loser, _) = Pair();
        var bystander = NewTarget("M 42");
        var dark = NewFrame(bystander.Id, configure: image => image.ImageType = "DARK");

        _repository.Merge(winner.Id, loser.Id);

        Assert.Equal(bystander.Id, FrameTargetOf(dark.Id));
    }

    // ---- unresolved-name merge ---------------------------------------------------------

    [Fact]
    public void MergeUnresolvedName_AssignsTheUnresolvedFramesOfThatName()
    {
        var winner = NewTarget("M 31");
        var mine = NewUnresolvedFrame(LibrarySeeder.RawHeadersWithObject("Andromeda"));
        var other = NewUnresolvedFrame(LibrarySeeder.RawHeadersWithObject("Sh2-155"));

        var result = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal(1, result.FramesMoved);
        Assert.Equal(winner.Id, FrameTargetOf(mine.Id));
        Assert.Null(FrameTargetOf(other.Id));
    }

    [Fact]
    public void MergeUnresolvedName_MatchesANumericObjectCard()
    {
        var winner = NewTarget("NGC 7331");

        // Phase 5 fix F1: an unquoted numeric OBJECT card comes back from json_extract as an
        // integer, so the group-key expression is the only correct way to select it.
        var frame = NewUnresolvedFrame("""{"OBJECT":7331}""");

        var result = _repository.MergeUnresolvedName(winner.Id, "7331");

        Assert.Equal(1, result.FramesMoved);
        Assert.Equal(winner.Id, FrameTargetOf(frame.Id));
    }

    [Fact]
    public void MergeUnresolvedName_AddsTheNameAsAnAlias()
    {
        var winner = NewTarget("M 31");

        var result = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        Assert.Equal(["Andromeda"], AliasesOf(ReadTarget(winner.Id)));
        Assert.Equal(["Andromeda"], result.AliasesAdded);
    }

    [Fact]
    public void MergeUnresolvedName_WritesAManifestWithANullLoserId()
    {
        var winner = NewTarget("M 31");
        var frame = NewUnresolvedFrame(LibrarySeeder.RawHeadersWithObject("Andromeda"));

        var result = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        // Ruling Q8: every merge writes a manifest, so the assignment is undoable from the same
        // history list.
        var manifest = Assert.Single(Manifests());
        Assert.Equal(result.ManifestId, manifest.Id);
        Assert.Null(manifest.LoserId);
        Assert.Equal(winner.Id, manifest.WinnerId);
        var payload = PayloadOf(manifest.Id);
        Assert.Equal([frame.Id], payload.MovedImageIds);
        Assert.Equal("Andromeda", payload.SourceName);
    }

    [Fact]
    public void MergeUnresolvedName_AcceptsTheOrphanCandidateAndPointsItAtTheWinner()
    {
        var winner = NewTarget("M 31");
        var orphan = NewCandidate("Andromeda", suggested: null);

        _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        var stored = ReadCandidate(orphan.Id);
        Assert.Equal("accepted", stored.Status);
        Assert.NotNull(stored.ResolvedAt);

        // The history row can then name the winner.
        Assert.Equal(winner.Id, stored.SuggestedTargetId);
    }

    [Fact]
    public void MergeUnresolvedName_LeavesAlreadyResolvedFramesAlone()
    {
        var winner = NewTarget("M 31");
        var bystander = NewTarget("M 42");
        var resolved = NewFrame(bystander.Id, configure: image =>
            image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Andromeda"));

        var result = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        Assert.Equal(0, result.FramesMoved);
        Assert.Equal(bystander.Id, FrameTargetOf(resolved.Id));
    }

    [Fact]
    public void MergeUnresolvedName_NoMatchingFrames_StillAddsTheAliasAndReportsZero()
    {
        var winner = NewTarget("M 31");

        var result = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal(0, result.FramesMoved);
        Assert.Equal(["Andromeda"], AliasesOf(ReadTarget(winner.Id)));
    }

    // ---- unmerge -----------------------------------------------------------------------

    [Fact]
    public void Unmerge_RestoresTheExactPreMergeState()
    {
        var (winner, loser, frames) = Pair(loserFrames: 3);
        var winnerNote = NewNote(winner.Id, 4, "winner text");
        var loserCollidingNote = NewNote(loser.Id, 4, "loser text");
        var loserOwnNote = NewNote(loser.Id, 5, "another night");
        var candidate = NewCandidate("Andromeda", winner.Id);
        var before = Snapshot(winner.Id, loser.Id, frames, [winnerNote.Id, loserCollidingNote.Id, loserOwnNote.Id], candidate.Id);

        _repository.Merge(winner.Id, loser.Id);
        var result = _repository.Unmerge(loser.Id);

        Assert.Equal(UnmergeStatus.Unmerged, result.Status);
        Assert.Equal(before, Snapshot(winner.Id, loser.Id, frames, [winnerNote.Id, loserCollidingNote.Id, loserOwnNote.Id], candidate.Id));
    }

    /// <summary>Every field a merge can change, as one comparable string. <c>updated_at</c> is
    /// deliberately absent: the manifest does not record it, and a restored note is re-stamped.
    /// </summary>
    private string Snapshot(Guid winnerId, Guid loserId, IEnumerable<Guid> frameIds, IEnumerable<Guid> noteIds, Guid candidateId)
    {
        var winner = ReadTarget(winnerId);
        var loser = ReadTarget(loserId);
        var lines = new List<string>
        {
            $"winner aliases={string.Join("|", AliasesOf(winner))}",
            $"loser merged_into={loser.MergedIntoId?.ToString() ?? "null"} merged_at={loser.MergedAt?.ToString("O") ?? "null"}",
        };
        lines.AddRange(frameIds.Select(id => $"frame {id}={FrameTargetOf(id)}"));
        lines.AddRange(noteIds.Select(id =>
        {
            var note = ReadNote(id);
            return $"note {id} target={note.TargetId} date={note.SessionDate:O} text={note.Notes}";
        }));
        var candidate = ReadCandidate(candidateId);
        lines.Add($"candidate {candidateId} status={candidate.Status} resolved={candidate.ResolvedAt?.ToString("O") ?? "null"}");
        return string.Join("\n", lines);
    }

    [Fact]
    public void Unmerge_MovesTheRecordedFramesBack()
    {
        var (winner, loser, frames) = Pair(loserFrames: 3);
        _repository.Merge(winner.Id, loser.Id);

        var result = _repository.Unmerge(loser.Id);

        Assert.Equal(3, result.FramesRestored);
        Assert.All(frames, id => Assert.Equal(loser.Id, FrameTargetOf(id)));
    }

    [Fact]
    public void Unmerge_LeavesAFrameReassignedElsewhereAlone()
    {
        var (winner, loser, frames) = Pair(loserFrames: 2);
        var bystander = NewTarget("M 42");
        _repository.Merge(winner.Id, loser.Id);

        using (var context = Open())
        {
            var moved = context.Images.Single(row => row.Id == frames[0]);
            moved.ResolvedTargetId = bystander.Id;
            context.SaveChanges();
        }

        var result = _repository.Unmerge(loser.Id);

        Assert.Equal(1, result.FramesRestored);
        Assert.Equal(bystander.Id, FrameTargetOf(frames[0]));
        Assert.Equal(loser.Id, FrameTargetOf(frames[1]));
    }

    [Fact]
    public void Unmerge_RestoresARekeyedNoteToTheLoser()
    {
        var (winner, loser, _) = Pair();
        var note = NewNote(loser.Id, 4, "clear, no moon");
        _repository.Merge(winner.Id, loser.Id);

        _repository.Unmerge(loser.Id);

        var stored = ReadNote(note.Id);
        Assert.Equal(loser.Id, stored.TargetId);
        Assert.Equal("clear, no moon", stored.Notes);
    }

    [Fact]
    public void Unmerge_RestoresAnAppendedNoteToItsPriorText()
    {
        var (winner, loser, _) = Pair();
        var winnerNote = NewNote(winner.Id, 4, "winner text");
        var loserNote = NewNote(loser.Id, 4, "loser text");
        _repository.Merge(winner.Id, loser.Id);

        _repository.Unmerge(loser.Id);

        Assert.Equal("winner text", ReadNote(winnerNote.Id).Notes);
        Assert.Equal(loser.Id, ReadNote(loserNote.Id).TargetId);
    }

    [Fact]
    public void Unmerge_RemovesTheLoserNamesFromTheWinnerAliases()
    {
        var (winner, loser, _) = Pair();
        _repository.Merge(winner.Id, loser.Id);

        var result = _repository.Unmerge(loser.Id);

        Assert.Empty(AliasesOf(ReadTarget(winner.Id)));
        Assert.Equal(["NGC 224", "Andromeda"], result.AliasesRemoved);
    }

    [Fact]
    public void Unmerge_KeepsAnAliasTheWinnerAlreadyHad()
    {
        // Ruling Q9: unmerge removes exactly the strings the manifest recorded as added, never a
        // set re-derived from the loser's names, which would strip an alias the winner carried
        // before the merge.
        var winner = NewTarget("M 31", "Andromeda");
        var loser = NewTarget("NGC 224", "Andromeda");
        _repository.Merge(winner.Id, loser.Id);

        var result = _repository.Unmerge(loser.Id);

        Assert.Equal(["Andromeda"], AliasesOf(ReadTarget(winner.Id)));
        Assert.Equal(["NGC 224"], result.AliasesRemoved);
    }

    [Fact]
    public void Unmerge_ClearsMergedIntoIdAndMergedAt()
    {
        var (winner, loser, _) = Pair();
        _repository.Merge(winner.Id, loser.Id);

        _repository.Unmerge(loser.Id);

        var stored = ReadTarget(loser.Id);
        Assert.Null(stored.MergedIntoId);
        Assert.Null(stored.MergedAt);
    }

    [Fact]
    public void Unmerge_ResetsAcceptedCandidatesToPending()
    {
        var (winner, loser, _) = Pair();
        var named = NewCandidate("Andromeda", winner.Id);
        var pointed = NewCandidate("whatever", loser.Id);
        _repository.Merge(winner.Id, loser.Id);

        _repository.Unmerge(loser.Id);

        foreach (var id in new[] { named.Id, pointed.Id })
        {
            var stored = ReadCandidate(id);
            Assert.Equal("pending", stored.Status);
            Assert.Null(stored.ResolvedAt);
        }
    }

    [Fact]
    public void Unmerge_DeletesTheManifest()
    {
        var (winner, loser, _) = Pair();
        _repository.Merge(winner.Id, loser.Id);

        _repository.Unmerge(loser.Id);

        // Spec 5.11: "Consumed and deleted by unmerge."
        Assert.Empty(Manifests());
    }

    [Fact]
    public void Unmerge_ASecondTime_IsANoOp()
    {
        var (winner, loser, frames) = Pair();
        var note = NewNote(loser.Id, 4, "clear, no moon");
        var candidate = NewCandidate("Andromeda", winner.Id);
        _repository.Merge(winner.Id, loser.Id);
        _repository.Unmerge(loser.Id);
        var after = Snapshot(winner.Id, loser.Id, frames, [note.Id], candidate.Id);

        var second = _repository.Unmerge(loser.Id);

        // Spec 18.1: "a second unmerge is a no-op".
        Assert.Equal(UnmergeStatus.NotMerged, second.Status);
        Assert.Equal(0, second.FramesRestored);
        Assert.Empty(second.AliasesRemoved);
        Assert.Equal(after, Snapshot(winner.Id, loser.Id, frames, [note.Id], candidate.Id));
    }

    [Fact]
    public void Unmerge_WithNoManifest_RestoresNothingAndReportsNoManifest()
    {
        var (winner, loser, frames) = Pair();
        _repository.Merge(winner.Id, loser.Id);
        using (var context = Open())
        {
            context.MergeManifests.RemoveRange(context.MergeManifests);
            context.SaveChanges();
        }

        var result = _repository.Unmerge(loser.Id);

        // Spec 9.7: there is no fallback path.
        Assert.Equal(UnmergeStatus.NoManifest, result.Status);
        Assert.All(frames, id => Assert.Equal(winner.Id, FrameTargetOf(id)));
        Assert.Equal(winner.Id, ReadTarget(loser.Id).MergedIntoId);
        Assert.Equal(["NGC 224", "Andromeda"], AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void Unmerge_WithACorruptPayload_RestoresNothingAndKeepsTheManifest()
    {
        var (winner, loser, frames) = Pair();
        _repository.Merge(winner.Id, loser.Id);
        using (var context = Open())
        {
            context.MergeManifests.Single().Payload = "{ not json";
            context.SaveChanges();
        }

        var result = _repository.Unmerge(loser.Id);

        // A manifest that cannot be read cannot be proved consumed, so it is kept.
        Assert.Equal(UnmergeStatus.ManifestUnreadable, result.Status);
        Assert.Single(Manifests());
        Assert.All(frames, id => Assert.Equal(winner.Id, FrameTargetOf(id)));
        Assert.Equal(winner.Id, ReadTarget(loser.Id).MergedIntoId);
    }

    [Fact]
    public void Unmerge_UnknownTarget_ReturnsNotFound()
    {
        var result = _repository.Unmerge(Guid.NewGuid());

        Assert.Equal(UnmergeStatus.NotFound, result.Status);
    }

    [Fact]
    public void Unmerge_TargetThatWasNeverMerged_ReturnsNotMerged()
    {
        var target = NewTarget("M 31");

        var result = _repository.Unmerge(target.Id);

        Assert.Equal(UnmergeStatus.NotMerged, result.Status);
    }

    // ---- chained merges undo in reverse order (F17) ------------------------------------

    [Fact]
    public void Unmerge_WhenTheWinnerHasItselfBeenMergedAway_IsRefusedAndWritesNothing()
    {
        // B into A, then A into C. B's frames live on C now, so restoring them from B's manifest
        // would take frames off C that this merge never moved.
        var a = NewTarget("A");
        var b = NewTarget("B");
        var c = NewTarget("C");
        var frame = NewFrame(b.Id);
        Assert.Equal(MergeStatus.Merged, _repository.Merge(a.Id, b.Id).Status);
        Assert.Equal(MergeStatus.Merged, _repository.Merge(c.Id, a.Id).Status);
        Assert.Equal(c.Id, FrameTargetOf(frame.Id));

        var refused = _repository.Unmerge(b.Id);

        Assert.Equal(UnmergeStatus.WinnerMergedAway, refused.Status);
        Assert.Equal("C", refused.WinnerMergedIntoName);
        Assert.Equal(0, refused.FramesRestored);

        // Nothing was written: both manifests stand, both losers are still merged away, and the
        // frame is still on C.
        Assert.Equal(2, Manifests().Count);
        Assert.Equal(a.Id, ReadTarget(b.Id).MergedIntoId);
        Assert.Equal(c.Id, ReadTarget(a.Id).MergedIntoId);
        Assert.Equal(c.Id, FrameTargetOf(frame.Id));
    }

    [Fact]
    public void Unmerge_ChainedMergesInReverseOrder_RestoreBothExactly()
    {
        var a = NewTarget("A");
        var b = NewTarget("B");
        var c = NewTarget("C");
        var bFrame = NewFrame(b.Id, day: 1);
        var aFrame = NewFrame(a.Id, day: 2);
        _repository.Merge(a.Id, b.Id);
        _repository.Merge(c.Id, a.Id);

        Assert.Equal(UnmergeStatus.Unmerged, _repository.Unmerge(a.Id).Status);
        Assert.Equal(UnmergeStatus.Unmerged, _repository.Unmerge(b.Id).Status);

        // Exactly where they started: every frame back on its own target, no target merged away,
        // no manifest left, and no alias of a merge that has been undone.
        Assert.Equal(b.Id, FrameTargetOf(bFrame.Id));
        Assert.Equal(a.Id, FrameTargetOf(aFrame.Id));
        Assert.Null(ReadTarget(a.Id).MergedIntoId);
        Assert.Null(ReadTarget(b.Id).MergedIntoId);
        Assert.Empty(Manifests());
        Assert.Empty(AliasesOf(ReadTarget(a.Id)));
        Assert.Empty(AliasesOf(ReadTarget(c.Id)));
    }

    [Fact]
    public void UndoUnresolvedNameMerge_WhenTheWinnerHasBeenMergedAway_IsRefusedAndWritesNothing()
    {
        var winner = NewTarget("M 31");
        var later = NewTarget("Andromeda Galaxy");
        var frame = NewUnresolvedFrame(LibrarySeeder.RawHeadersWithObject("Andromeda"));
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");
        Assert.Equal(MergeStatus.Merged, _repository.Merge(later.Id, winner.Id).Status);

        var refused = _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        Assert.Equal(UnmergeStatus.WinnerMergedAway, refused.Status);
        Assert.Equal("Andromeda Galaxy", refused.WinnerMergedIntoName);
        Assert.Equal(0, refused.FramesRestored);

        // The frame stayed with the later winner and both manifests stand.
        Assert.Equal(later.Id, FrameTargetOf(frame.Id));
        Assert.Equal(2, Manifests().Count);
    }

    [Fact]
    public void UndoUnresolvedNameMerge_ReturnsTheFramesToUnresolved()
    {
        var winner = NewTarget("M 31");
        var frame = NewUnresolvedFrame(LibrarySeeder.RawHeadersWithObject("Andromeda"));
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        var result = _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        Assert.Equal(UnmergeStatus.Unmerged, result.Status);
        Assert.Equal(1, result.FramesRestored);
        Assert.Null(FrameTargetOf(frame.Id));
    }

    [Fact]
    public void UndoUnresolvedNameMerge_RemovesTheAliasWhenTheCandidateNamesIt()
    {
        var winner = NewTarget("M 31");
        var orphan = NewCandidate("Andromeda", suggested: null);
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        var result = _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        Assert.Equal(["Andromeda"], result.AliasesRemoved);
        Assert.Empty(AliasesOf(ReadTarget(winner.Id)));
        var stored = ReadCandidate(orphan.Id);
        Assert.Equal("pending", stored.Status);
        Assert.Null(stored.ResolvedAt);
    }

    [Fact]
    public void UndoUnresolvedNameMerge_RemovesTheAliasWithNoCandidateRow()
    {
        // Ruling Q9: the removal set comes from the manifest's aliases_added, so an assignment
        // made with no merge_candidates row behind it still undoes cleanly.
        var winner = NewTarget("M 31");
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        var result = _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        Assert.Equal(["Andromeda"], result.AliasesRemoved);
        Assert.Empty(AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void UndoUnresolvedNameMerge_DeletesTheManifest()
    {
        var winner = NewTarget("M 31");
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");

        _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        Assert.Empty(Manifests());
    }

    [Fact]
    public void Merge_WritesANullSourceNameInTheManifest()
    {
        var (winner, loser, _) = Pair();

        var result = _repository.Merge(winner.Id, loser.Id);

        // source_name carries the absorbed OBJECT string of an unresolved-name merge, and is null
        // for a target merge, which has a loser_id instead.
        Assert.Null(PayloadOf(result.ManifestId!.Value).SourceName);
    }

    [Fact]
    public void UndoUnresolvedNameMerge_RevertsTheCandidateWhenTheAliasWasAlreadyPresent()
    {
        // Review finding 5: a merge whose alias the winner already carried records no
        // aliases_added, so the candidate rows can only be found through source_name.
        var winner = NewTarget("M 31", "Andromeda");
        var candidate = NewCandidate("Andromeda", winner.Id, method: "trigram");
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");
        Assert.Empty(merge.AliasesAdded);

        _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        var stored = ReadCandidate(candidate.Id);
        Assert.Equal("pending", stored.Status);
        Assert.Null(stored.ResolvedAt);

        // A trigram candidate suggested the winner on its own, so the undo leaves that suggestion.
        Assert.Equal(winner.Id, stored.SuggestedTargetId);
        Assert.Equal(["Andromeda"], AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void UndoUnresolvedNameMerge_OrphanCandidate_LosesTheSuggestedTarget()
    {
        var winner = NewTarget("M 31");
        var orphan = NewCandidate("Andromeda", suggested: null);
        var merge = _repository.MergeUnresolvedName(winner.Id, "Andromeda");
        Assert.Equal(winner.Id, ReadCandidate(orphan.Id).SuggestedTargetId);

        _repository.UndoUnresolvedNameMerge(merge.ManifestId!.Value);

        // The merge wrote the suggestion onto an orphan row, so the undo takes it back off.
        var stored = ReadCandidate(orphan.Id);
        Assert.Equal("pending", stored.Status);
        Assert.Null(stored.SuggestedTargetId);
    }

    // ---- the alias union rule, called directly -----------------------------------------

    [Fact]
    public void AbsorbAliases_IsTheSingleOwnerOfTheCaseInsensitiveUnion()
    {
        // Public so Task 4's preview computes AliasesToAdd from this method rather than a second
        // implementation that eventually disagrees with the merge (review finding 1).
        var winner = new Target { Id = Guid.NewGuid(), PrimaryName = "M 31", Aliases = "[\"andromeda\"]" };

        var (aliases, added) = MergeRepository.AbsorbAliases(winner, ["NGC 224", "Andromeda", "m 31", "NGC 224"]);

        Assert.Equal(["andromeda", "NGC 224"], aliases);
        Assert.Equal(["NGC 224"], added);

        // The call is pure: it reports what a merge would add and writes nothing.
        Assert.Equal("[\"andromeda\"]", winner.Aliases);
    }

    // ---- round trip --------------------------------------------------------------------

    [Fact]
    public void MergeThenUnmerge_LeavesTheTargetsUniqueIndexUsable()
    {
        var (winner, loser, _) = Pair();
        _repository.Merge(winner.Id, loser.Id);
        _repository.Unmerge(loser.Id);

        // What the spec 5.3 partial unique indexes exist for: the loser is active again, keeps
        // its own primary_name, and the same pair merges a second time.
        var again = _repository.Merge(winner.Id, loser.Id);

        Assert.Equal(MergeStatus.Merged, again.Status);
        Assert.Equal(["NGC 224", "Andromeda"], AliasesOf(ReadTarget(winner.Id)));
    }

    [Fact]
    public void MergeThenUnmerge_OnTheSeededLibrary_MovesNoOtherTargetTotals()
    {
        LibrarySeeder.Seed(_db.ConnectionString);
        var before = FrameCountsByTarget();

        _repository.Merge(LibrarySeeder.Targets[1].Id, LibrarySeeder.Targets[0].Id);
        var midway = FrameCountsByTarget();
        _repository.Unmerge(LibrarySeeder.Targets[0].Id);

        Assert.Equal(before, FrameCountsByTarget());

        // The two merged targets are the only totals that moved at all.
        foreach (var seeded in LibrarySeeder.Targets.Skip(2))
        {
            Assert.Equal(before[seeded.Id], midway[seeded.Id]);
        }
    }

    private Dictionary<Guid, int> FrameCountsByTarget()
    {
        using var context = Open();
        return context.Images
            .Where(image => image.ResolvedTargetId != null)
            .GroupBy(image => image.ResolvedTargetId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
    }
}
