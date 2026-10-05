using System.Text.Json;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Repositories;

/// <summary>The outcome of <see cref="TargetWriteRepository.Rename"/>. A name collision is an
/// outcome rather than an exception, so the page reports it as a message instead of failing on
/// the UI thread.</summary>
public enum RenameOutcome
{
    /// <summary><c>primary_name</c> was changed and <c>name_locked</c> set.</summary>
    Renamed,

    /// <summary>Another unmerged target already carries that <c>primary_name</c>. Nothing was
    /// written.</summary>
    NameTaken,

    /// <summary>No target row with that id. Nothing was written.</summary>
    NotFound,
}

/// <summary>The outcome of <see cref="TargetWriteRepository.SetObjectType"/>. There is no unique
/// index over <c>object_type</c>, so there is no collision member and no caught constraint: the
/// only thing that can go wrong is a target that is no longer there.</summary>
public enum SetObjectTypeOutcome
{
    /// <summary><c>object_type</c> was set to the chosen category's first SIMBAD code.</summary>
    Changed,

    /// <summary>No target row with that id. Nothing was written.</summary>
    NotFound,
}

/// <summary>
/// The three database writes Target detail performs itself. Database rows only: nothing in this
/// type touches the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Takes the DI <see cref="DatabaseConnectionString"/> and opens its own short-lived tracking
/// context per call, matching <c>ScanRunRepository</c> and <c>ActivityRepository</c>. It is a
/// repository, not a query, so it lives here and not in <c>Queries</c>.
/// </para>
/// <para>
/// One of three App-layer writers of <c>targets</c> rows (questions.md Q20): this type,
/// <c>MergeRepository</c> and <c>TargetEnrichmentRepository</c>. These three methods are
/// therefore not the complete list of what can change a target the page is showing: a merge
/// writes <c>merged_into_id</c>, re-enrichment writes the catalog columns, and
/// <c>TargetResolver</c> writes rows as well, driven by the scan and by spec 9.7's
/// unresolved-name retry rather than by any App surface.
/// </para>
/// </remarks>
public sealed class TargetWriteRepository(
    DatabaseConnectionString connectionString,
    ILogger? logger = null)
{
    /// <summary>
    /// Sets <c>primary_name</c> and <c>name_locked = 1</c> (spec 5.3: name_locked is "set when
    /// the user renames a target"), so later catalog enrichment does not overwrite the user's
    /// choice.
    /// </summary>
    /// <remarks>
    /// <c>primary_name</c> is unique among unmerged targets (spec 5.3, enforced by the partial
    /// unique index), so a collision comes back as <see cref="RenameOutcome.NameTaken"/> from the
    /// caught constraint violation rather than as an exception. The violation is caught rather
    /// than pre-checked because a pre-check is a second round trip that is still racy; the index
    /// is the authority either way. SQLite rolls the failed <c>SaveChanges</c> back, so nothing
    /// is half-written.
    /// </remarks>
    public RenameOutcome Rename(Guid targetId, string primaryName)
    {
        using var context = Open();
        var target = context.Targets.SingleOrDefault(row => row.Id == targetId);
        if (target is null)
        {
            return RenameOutcome.NotFound;
        }

        // Read before the assignment: the tracked entity is the only place the old name exists
        // once it is overwritten.
        var previousName = target.PrimaryName;

        target.PrimaryName = primaryName;
        target.NameLocked = true;

        // Spec 12.7's rename history, read back by RenameHistoryQuery (questions.md Q11): there
        // is no rename_history table, and activity_events is this port's only durable per-action
        // record. Added to the SAME SaveChanges as the rename, so a rename that loses to the
        // unique index records nothing.
        ActivityRepository.Emit(
            context, category: "user_action", severity: "info",
            eventType: RenameHistoryQuery.RenameEventType,
            message: $"Renamed \"{previousName}\" to \"{primaryName}\"",
            details: new { previous_name = previousName, new_name = primaryName },
            targetId: targetId);

        try
        {
            context.SaveChanges();
            return RenameOutcome.Renamed;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            return RenameOutcome.NameTaken;
        }
    }

    /// <summary>
    /// Spec 12.4's object type edit (PAR-009): sets <c>targets.object_type</c> to
    /// <paramref name="category"/>'s first SIMBAD code and emits a <c>user_action</c> /
    /// <c>target_object_type_changed</c> activity event carrying the target id, the old values and
    /// the new ones, in one <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing else is written. The catalogue columns a re-resolve fills (spec 9.7) are untouched,
    /// and <c>name_locked</c> is left exactly as it was: a user's type choice is not a name lock,
    /// so a later re-resolve overwrites the object type the same way it overwrites every other
    /// catalogue column (spec 12.4). <c>Rename</c> sets <c>name_locked</c> and the reasons are
    /// different.
    /// </para>
    /// <para>
    /// Spec 12.4 also says the write sets <c>targets.category</c>. This schema has no such column:
    /// the display category is derived at every read by
    /// <see cref="ObjectTypeCategories.Categorize"/> (spec 9.8), which is what the dashboard's
    /// Object Type pills, the Statistics breakdowns and this page all call. Writing the code is
    /// therefore the whole write, and the category follows it everywhere at the next read, which
    /// is the sentence's intent. <c>SetObjectType_RoundTripsThroughCategorize</c> is the pin.
    /// </para>
    /// </remarks>
    /// <param name="category">One of spec 9.8's display categories, or <c>Other</c>. A category
    /// this map does not know is treated as <c>Other</c> rather than rejected, which is the same
    /// rule <see cref="ObjectTypeCategories.Categorize"/> applies in the other direction.</param>
    public SetObjectTypeOutcome SetObjectType(Guid targetId, string category)
    {
        using var context = Open();
        var target = context.Targets.SingleOrDefault(row => row.Id == targetId);
        if (target is null)
        {
            return SetObjectTypeOutcome.NotFound;
        }

        // Read before the assignment, exactly as Rename does: the tracked entity is the only place
        // the old values exist once object_type is overwritten.
        var previousObjectType = target.ObjectType;
        var previousCategory = ObjectTypeCategories.Categorize(previousObjectType);

        var objectType = FirstSimbadCode(category);
        target.ObjectType = objectType;

        // HANDOFF rule 9: an activity event's details document is snake_case. Added to the SAME
        // SaveChanges as the write, so a write that fails records nothing.
        ActivityRepository.Emit(
            context, category: "user_action", severity: "info",
            eventType: ObjectTypeChangedEventType,
            message: $"Set object type to \"{category}\"",
            details: new
            {
                previous_object_type = previousObjectType,
                previous_category = previousCategory,
                new_object_type = objectType,
                new_category = ObjectTypeCategories.Categorize(objectType),
            },
            targetId: targetId);

        context.SaveChanges();
        return SetObjectTypeOutcome.Changed;
    }

    /// <summary>The <c>event_type</c> <see cref="SetObjectType"/> writes, beside
    /// <c>RenameHistoryQuery.RenameEventType</c>'s own purpose: one constant, so a writer and any
    /// later reader cannot drift.</summary>
    /// <remarks>It lives here rather than on a query because, unlike the rename history, nothing
    /// reads these events back by type yet. Spec 12.4 names the literal and the Activity page
    /// renders it like any other <c>user_action</c> row.</remarks>
    public const string ObjectTypeChangedEventType = "target_object_type_changed";

    /// <summary>
    /// Spec 9.8's table read backwards: the first SIMBAD code listed for each display category,
    /// which is what spec 12.4 says the edit stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A small table of its own rather than a reversal of <c>ObjectTypeCategories</c>' map: that
    /// map is many codes to one category and its own order is not a promise, and giving it a
    /// reverse lookup would mean editing a file three surfaces read. The drift risk the duplicate
    /// carries is closed by a test rather than by a comment:
    /// <c>SetObjectType_RoundTripsThroughCategorize</c> drives every entry here through
    /// <see cref="ObjectTypeCategories.Categorize"/> and asserts it lands back on its own category,
    /// so a code that stopped mapping fails the build's test pass.
    /// </para>
    /// <para>
    /// <c>Other</c> has no SIMBAD code, and spec 9.8 says "anything mapped by no code is Other",
    /// so it stores the empty string: <c>Categorize</c> returns <c>Other</c> for an empty or null
    /// <c>object_type</c>, which is the round trip that has to hold.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> FirstSimbadCodes = new(StringComparer.Ordinal)
    {
        ["Emission Nebula"] = "HII",
        ["Reflection Nebula"] = "GNe",
        ["Dark Nebula"] = "DNe",
        ["Planetary Nebula"] = "PN",
        ["Supernova Remnant"] = "SNR",
        ["Galaxy"] = "G",
        ["Open Cluster"] = "OpC",
        ["Globular Cluster"] = "GlC",
        ["Star"] = "*",
        [OtherCategory] = "",
    };

    /// <summary>Spec 9.8's catch-all category, which no SIMBAD code maps to.</summary>
    public const string OtherCategory = "Other";

    /// <summary>Spec 9.8's table read backwards: the first SIMBAD code listed for
    /// <paramref name="category"/>, or the empty string for <c>Other</c> and for any category this
    /// map does not know.</summary>
    /// <remarks>Public and static because <c>TargetHeaderViewModel</c> shows the new value the
    /// moment the write returns, rather than waiting for a reload (spec 12.4), and a second copy
    /// of this table in the view-model is the drift this method exists to prevent. It reads no
    /// database and touches no state.</remarks>
    public static string FirstSimbadCode(string category)
        => FirstSimbadCodes.TryGetValue(category, out var code) ? code : "";

    /// <summary>Sets <c>targets.notes</c>. Null clears it.</summary>
    public void SaveTargetNotes(Guid targetId, string? notes)
    {
        using var context = Open();
        var target = context.Targets.SingleOrDefault(row => row.Id == targetId);
        if (target is null)
        {
            return;
        }

        target.Notes = notes;
        context.SaveChanges();
    }

    /// <summary>
    /// Upserts the <c>session_notes</c> row for (target, date) and stamps <c>updated_at</c>; null
    /// or blank text deletes the row, which is what keeps the notes indicator honest and is a
    /// database delete, never a file one. Unique on (target_id, session_date) per spec 5.9, so
    /// the upsert is a find-then-insert-or-update inside one <c>SaveChanges</c>.
    /// </summary>
    public void SaveSessionNotes(Guid targetId, DateOnly sessionDate, string? notes)
    {
        using var context = Open();
        var existing = context.SessionNotes
            .SingleOrDefault(row => row.TargetId == targetId && row.SessionDate == sessionDate);

        if (string.IsNullOrWhiteSpace(notes))
        {
            if (existing is not null)
            {
                context.SessionNotes.Remove(existing);
                context.SaveChanges();
            }

            return;
        }

        if (existing is null)
        {
            context.SessionNotes.Add(new SessionNote
            {
                Id = Guid.NewGuid(),
                TargetId = targetId,
                SessionDate = sessionDate,
                Notes = notes,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Notes = notes;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        context.SaveChanges();
    }

    /// <summary>
    /// Spec 9.7's manual creation (PAR-001): inserts the target spec 12.7's Create target form
    /// describes, then retro-links the unresolved frames and closes the pending merge candidates
    /// that carry one of its names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No catalog enrichment runs here and no network call is made, which spec 9.7 states in as
    /// many words: this type holds no resolver reference and this method takes no resolver
    /// delegate, so "no network call" holds by construction rather than by a stub.
    /// </para>
    /// <para>
    /// Two <c>SaveChanges</c> calls rather than this class's usual one, and deliberately in this
    /// order. The retro-link's frame update is raw SQL on the context's own connection
    /// (<c>UnresolvedObjects.AssignFrames</c> is the one implementation of it in the assembly), so
    /// it commits the moment it runs and cannot be part of the insert's batch. Spec 9.7's own step
    /// order is insert (step 4) then retro-link (step 5), which is the only order that cannot
    /// leave a frame pointing at a target row that does not exist. The counts the event carries
    /// are the retro-link's, so the event is added after it and lands in the second save beside
    /// the closed candidates. A refusal returns before either save, so it writes nothing at all,
    /// which is what <c>Rename</c>'s emit-before-save achieves for its own case.
    /// </para>
    /// <para>
    /// The two conflicts are checked before the insert rather than caught from the unique indexes:
    /// the alias half of the first refusal has no index behind it, and both sentences name the
    /// conflicting target, which a <c>SqliteException</c> does not carry. There is no caught
    /// constraint as a backstop, for spec 9.7's own reason in the resolver's case: resolution and
    /// this form are the only writers of <c>targets</c> and neither runs concurrently with the
    /// other, so a unique violation surviving the pre-check is a genuine defect and propagates.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The primary name is empty or blank. Spec 9.7 step 1's
    /// refusal is the form's (spec 12.7), so an empty name reaching here is a defect.</exception>
    public CreateTargetResult CreateUserDefined(CreateTargetRequest request)
    {
        // Step 1. The refusal itself lives in CreateTargetViewModel; this is the defect guard.
        var primaryName = request.PrimaryName.Trim();
        if (primaryName.Length == 0)
        {
            throw new ArgumentException("The primary name is required.", nameof(request));
        }

        // Step 2.
        var aliases = BuildAliases(primaryName, request.Aliases);

        var catalogId = string.IsNullOrWhiteSpace(request.CatalogId) ? null : request.CatalogId.Trim();
        var catalogIdNormalized = NameNormalizer.NormalizeCatalogId(catalogId);

        using var context = Open();
        var targets = new TargetRepository(context);

        // Every name this target answers to, in the one comparison form the aliases column
        // already stores: trimmed, whitespace-collapsed, uppercase.
        var names = new HashSet<string>(aliases, StringComparer.Ordinal)
        {
            NameNormalizer.Normalize(primaryName),
        };

        // Step 3, over active targets only. The partial unique indexes are scoped to unmerged
        // rows for the same reason, so a merged-away target never causes a refusal.
        //
        // Phase 14B fixer, fixer list item 23 (task3-review P3), recording why this is one pass
        // rather than TargetRepository.FindByPrimaryName plus FindByAliasExact. Those two answer
        // "does any target carry THIS ONE name", and what step 3 has to answer is "does any active
        // target carry ANY of this form's names", which is the primary name plus every typed
        // alias, in both directions: the new name against their aliases and their name against
        // the new aliases. Through the two members that is two lookups per name in the set and it
        // still misses the second direction, because neither member takes an alias list. One pass
        // over the active set is the shape that answers the real question, and the active set is
        // the same set those members scan anyway.
        foreach (var existing in context.Targets.Where(row => row.MergedIntoId == null).ToList())
        {
            if (!names.Contains(NameNormalizer.Normalize(existing.PrimaryName))
                && !DeserializeAliases(existing.Aliases).Any(alias => names.Contains(NameNormalizer.Normalize(alias))))
            {
                continue;
            }

            return new CreateTargetResult(CreateTargetOutcome.NameInUse, null, existing.PrimaryName, 0, 0);
        }

        if (catalogIdNormalized is not null
            && targets.FindByCatalogIdNormalized(catalogIdNormalized) is { } catalogHolder)
        {
            return new CreateTargetResult(
                CreateTargetOutcome.CatalogIdInUse, null, catalogHolder.PrimaryName, 0, 0);
        }

        // Step 4. name_locked is not the checkbox: spec 9.7 sets it on every row this path
        // writes, and either flag alone stops an automatic pass re-resolving the row.
        var target = new Target
        {
            Id = Guid.NewGuid(),
            PrimaryName = primaryName,
            CatalogId = catalogId,
            CatalogIdNormalized = catalogIdNormalized,
            Aliases = JsonSerializer.Serialize(aliases),
            Ra = request.Ra,
            Dec = request.Dec,
            ObjectType = string.IsNullOrWhiteSpace(request.ObjectType) ? null : request.ObjectType.Trim(),
            NameLocked = true,
            UserDefined = request.UserDefined,
        };
        targets.Insert(target);
        context.SaveChanges();

        // Step 5, frame-driven. The same connection the context writes on, so
        // PragmaConnectionInterceptor and spec 5.1's busy timeout stay in the path (the shape
        // TargetRebuild.Run uses).
        //
        // Driven by the unresolved frames, not by the merge candidates (Phase 14B Task 3 review,
        // escalation 1, ruled a product correction). The list this form is opened from is
        // UnresolvedObjects.Read, which is frame-driven: a name whose candidate was dismissed, or
        // for which no candidate was ever written, still shows there with a frame count. Keying
        // the retro-link on merge_candidates left exactly those rows reporting "linked 0 frames
        // from 0 unresolved names" while their frames stayed unresolved. A pending candidate is
        // still closed when one exists, but one is never required.
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var linkedFrames = 0;
        var linkedNames = 0;
        foreach (var (unresolvedName, _) in UnresolvedObjects.Read(connection))
        {
            if (!names.Contains(NameNormalizer.Normalize(unresolvedName)))
            {
                continue;
            }

            var moved = UnresolvedObjects.AssignFrames(connection, unresolvedName, target.Id);
            if (moved == 0)
            {
                continue;
            }

            linkedFrames += moved;
            linkedNames++;
        }

        // A pending candidate for one of the new target's names is answered by the create
        // whether or not it still had frames to move, so it closes either way.
        foreach (var candidate in context.MergeCandidates.Where(row => row.Status == "pending").ToList())
        {
            if (!names.Contains(NameNormalizer.Normalize(candidate.SourceName)))
            {
                continue;
            }

            candidate.SuggestedTargetId = target.Id;
            candidate.Status = "accepted";
            candidate.ResolvedAt = DateTime.UtcNow;
        }

        // Step 6. HANDOFF rule 9: every key snake_case. Spec 10.9's payload is
        // {primary_name, catalog_id, source}; the two counts are this path's addition.
        ActivityRepository.Emit(
            context, category: "enrichment", severity: "info",
            eventType: TargetCreatedEventType,
            message: $"Created \"{primaryName}\"",
            details: new
            {
                primary_name = primaryName,
                catalog_id = catalogId,
                source = "manual",
                linked_frames = linkedFrames,
                closed_candidates = linkedNames,
            },
            targetId: target.Id);

        // Phase 14B fixer, fixer list item 22 (task3-review P3). Steps 4 and 5 are committed
        // before this save runs: the target row went in with its own SaveChanges above, because
        // the frame links need it for the foreign key, and those links are raw commands on the
        // same connection, which SQLite autocommits. A throw here therefore leaves a target that
        // EXISTS, and the caller reporting "the target could not be created" for it was false
        // twice over, because the reader's re-submit was then refused by the name conflict
        // against the row this very call had created. What is lost is the pending-candidate
        // closures and the target_created activity row, which is what the distinct outcome names.
        // There is no one-save alternative to return to instead: UnresolvedObjects runs its own
        // commands and Microsoft.Data.Sqlite refuses a command that does not carry the
        // connection's active transaction, so a single transaction over all of it would be a
        // change to that type's signatures and to its other five callers.
        try
        {
            context.SaveChanges();
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            // Fix-wave review: the form's sentence for this outcome says "See the log for
            // details", so there has to be a line in the log. The optional trailing ILogger is the
            // shape GalactiLog.Data's Ingest types already use (SmartRebuild, TargetRebuild): null
            // on every construction that does not pass one, so the two test call sites are
            // unchanged, and AppHost gives it the real one.
            logger?.LogWarning(
                exception,
                "The target {PrimaryName} was created and its frames linked, but the "
                + "target_created event and the pending merge candidates could not be written",
                primaryName);

            return new CreateTargetResult(
                CreateTargetOutcome.CreatedWithoutBookkeeping, target.Id, null, linkedFrames, linkedNames);
        }

        return new CreateTargetResult(
            CreateTargetOutcome.Created, target.Id, null, linkedFrames, linkedNames);
    }

    /// <summary>Spec 10.9's <c>target_created</c>, the one literal both this writer and any later
    /// reader use.</summary>
    public const string TargetCreatedEventType = "target_created";

    /// <summary>
    /// Spec 9.7 step 2, port of <c>_build_aliases</c>: the typed list in the order typed,
    /// deduplicated case-insensitively, then the primary name's normalized form.
    /// </summary>
    /// <remarks>
    /// Stored uppercase and panel-stripped, which is what <c>TargetResolver</c> appends and what
    /// <c>TargetRepository.FindByAliasExact</c> looks up: a differently cased alias is invisible to
    /// that lookup, so there is no second alias convention here. The literal primary string is not
    /// stored, because <c>primary_name</c> is matched on its own; its normalized form is, so a
    /// later scan whose <c>OBJECT</c> is the upper-case name links to this target.
    /// </remarks>
    private static List<string> BuildAliases(string primaryName, IReadOnlyList<string> typed)
    {
        var aliases = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in typed.Append(primaryName))
        {
            var normalized = NameNormalizer.StripPanel(NameNormalizer.Normalize(alias));
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                aliases.Add(normalized);
            }
        }

        return aliases;
    }

    private static List<string> DeserializeAliases(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    // SQLITE_CONSTRAINT. Microsoft.Data.Sqlite exposes the primary result code on
    // SqliteException.SqliteErrorCode; the extended code (2067, SQLITE_CONSTRAINT_UNIQUE) is on
    // SqliteExtendedErrorCode. Matching the primary code keeps this from depending on which
    // constraint form the index takes.
    private const int SqliteConstraint = 19;

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
}

/// <summary>The outcome of <see cref="TargetWriteRepository.CreateUserDefined"/> (spec 9.7's
/// manual creation, PAR-001). Both refusals are outcomes rather than exceptions, so the form
/// reports them as a message and keeps what the user typed.</summary>
public enum CreateTargetOutcome
{
    /// <summary>The row was inserted, <c>name_locked</c>, and the retro-link ran.</summary>
    Created,

    /// <summary>The name or one of its aliases is already carried by an active target, as its
    /// <c>primary_name</c> or as one of its aliases. Nothing was written.</summary>
    NameInUse,

    /// <summary>The normalized catalog id is already carried by an active target. Nothing was
    /// written.</summary>
    CatalogIdInUse,

    /// <summary>
    /// The row was inserted and the retro-link ran, but the pending merge candidates and the
    /// <c>target_created</c> activity row could not be written. The target exists and its frames
    /// are linked (Phase 14B fixer, fixer list item 22).
    /// </summary>
    CreatedWithoutBookkeeping,
}

/// <summary>What spec 9.7's manual creation did, or why it refused.</summary>
/// <param name="Outcome">Created, or which of the two refusals fired.</param>
/// <param name="TargetId">The new row's id, or null on a refusal.</param>
/// <param name="ConflictingTargetName">The conflicting target's <c>primary_name</c>, which is what
/// both refusal sentences name. Null on success.</param>
/// <param name="LinkedFrames">How many LIGHT frames the retro-link moved onto the new target.</param>
/// <param name="ClosedCandidates">How many distinct unresolved <c>OBJECT</c> names the retro-link
/// moved frames from, which is spec 12.7's "<c>from &lt;m&gt; unresolved names</c>". The member
/// keeps its original name because Task 4 reads this file's surface; its meaning is the frame-driven
/// name count since the Phase 14B Task 3 review, not a count of <c>merge_candidates</c> rows. A
/// pending candidate for one of those names is closed as accepted as well, but a create needs no
/// candidate at all.</param>
public sealed record CreateTargetResult(
    CreateTargetOutcome Outcome,
    Guid? TargetId,
    string? ConflictingTargetName,
    int LinkedFrames,
    int ClosedCandidates);

/// <summary>Spec 12.7's Create target form, as the repository takes it: already parsed, never as
/// text. The coordinate bounds and the empty-name refusal are the view-model's (spec 12.7); this
/// record carries the values that survived them.</summary>
/// <param name="PrimaryName">The name as typed. Trimmed here; an empty one is a defect, not a user
/// error, and throws.</param>
/// <param name="ObjectType">The chosen display category literal, the free text typed under Other,
/// or null when nothing was chosen.</param>
/// <param name="Ra">Degrees, 0 to 360 inclusive, or null.</param>
/// <param name="Dec">Degrees, -90 to 90 inclusive, or null.</param>
/// <param name="CatalogId">As typed, or null or blank for none.</param>
/// <param name="Aliases">The comma-separated list as typed, already split, in the order typed.</param>
/// <param name="UserDefined">The form's checkbox. <c>name_locked</c> is not a choice: spec 9.7
/// sets it on every row this path writes.</param>
public sealed record CreateTargetRequest(
    string PrimaryName,
    string? ObjectType,
    double? Ra,
    double? Dec,
    string? CatalogId,
    IReadOnlyList<string> Aliases,
    bool UserDefined);
