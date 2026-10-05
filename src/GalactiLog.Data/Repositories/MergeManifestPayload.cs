using System.Text.Json.Serialization;

namespace GalactiLog.Data.Repositories;

/// <summary>One entry of spec 5.11's <c>notes_appended</c> array: the winner note that absorbed a
/// loser note, and the text it carried before.</summary>
public sealed record AppendedNote(
    [property: JsonPropertyName("note_id")] Guid NoteId,
    [property: JsonPropertyName("prev_text")] string PrevText);

/// <summary>
/// Spec 5.11's <c>merge_manifests.payload</c>:
/// <c>{moved_image_ids: [], notes_rekeyed: [], notes_appended: [{note_id, prev_text}]}</c>, plus
/// the fourth key <c>aliases_added</c> (coordinator ruling Q9) and the fifth key
/// <c>source_name</c> (Task 2 review, finding 5). Written before anything moves and consumed by
/// unmerge; it is the only record of what a merge did, so its shape is the compatibility surface
/// of every merge on disk.
/// </summary>
/// <remarks>
/// <para>
/// Snake-cased property names, not the CLR ones: the column is read back by a future version of
/// this application and the names are the contract. Serialized with the default
/// <c>JsonSerializerOptions</c> plus the attributes; no naming policy, so an added property
/// cannot silently rename an existing one.
/// </para>
/// <para>
/// <c>aliases_added</c> records the alias strings the merge actually added to the winner, which
/// is what unmerge removes. Deriving the removal set from the loser's current names instead would
/// strip an alias the winner already carried before the merge, and would be wrong for both merge
/// shapes: an unresolved-name merge has no loser row to derive from at all.
/// </para>
/// <para>
/// <c>source_name</c> is null for a target merge and carries the absorbed <c>OBJECT</c> string
/// for the unresolved-name shape. It is what the undo matches <c>merge_candidates</c> rows on,
/// and what Task 5's history row labels a null-<c>loser_id</c> manifest with. Deriving either
/// from <c>aliases_added</c> fails whenever the winner already carried the name, because nothing
/// was then added.
/// </para>
/// <para>
/// A payload read back from an older or hand-edited row can be missing an array, which the
/// positional constructor takes as null however the property is annotated, so every read
/// coalesces to an empty list.
/// </para>
/// <para>
/// <c>custom_values_moved</c> (ruling C3, Phase 20) is the sixth key and the ids
/// <c>CustomColumnRepository.MoveValuesOnMerge</c> moved. Trailing and read as null by the
/// positional constructor on an older row, so it coalesces to an empty list exactly as the other
/// arrays do. Deriving it at unmerge time instead of recording it here would be wrong: the winner
/// may have acquired its own value in the same slot since the merge, so only the ids the merge
/// actually moved may move back. The loser values the merge left where they were are deliberately
/// NOT recorded beside them: a merge sets the loser's <c>merged_into_id</c> rather than deleting
/// the row, so a value in a slot the winner already held is never touched, never cascaded away and
/// is already where an unmerge needs it, and an array of them would be a second copy of the truth
/// that could disagree with the table.
/// </para>
/// </remarks>
public sealed record MergeManifestPayload(
    [property: JsonPropertyName("moved_image_ids")] IReadOnlyList<Guid> MovedImageIds,
    [property: JsonPropertyName("notes_rekeyed")] IReadOnlyList<Guid> NotesRekeyed,
    [property: JsonPropertyName("notes_appended")] IReadOnlyList<AppendedNote> NotesAppended,
    [property: JsonPropertyName("aliases_added")] IReadOnlyList<string> AliasesAdded,
    [property: JsonPropertyName("source_name")] string? SourceName,
    [property: JsonPropertyName("custom_values_moved")] IReadOnlyList<Guid> CustomValuesMoved);
