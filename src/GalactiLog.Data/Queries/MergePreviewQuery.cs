using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One side of spec 12.9's comparison. Every field the spec lists, and nothing
/// else.</summary>
/// <param name="TargetId">Null for an unresolved-<c>OBJECT</c> loser, which has no target row: its
/// <paramref name="PrimaryName"/> is the raw <c>OBJECT</c> string and every catalog field is
/// null.</param>
public sealed record MergePreviewSide(
    Guid? TargetId,
    string PrimaryName,
    string? CatalogId,
    IReadOnlyList<string> Aliases,
    string? ObjectType,
    string ObjectCategory,
    double? Ra,
    double? Dec,
    int FrameCount,
    double IntegrationSeconds,
    int SessionCount,
    DateOnly? FirstSession,
    DateOnly? LastSession);

/// <summary>Spec 12.9's preview.</summary>
/// <param name="Loser">Null when the dialog was opened from Target detail with only the winner
/// known and nothing chosen in the search box yet (deviation D1). Every figure below is then
/// empty, because there is no merge to describe.</param>
/// <param name="CollidingSessionDates">The dates on which both sides carry a
/// <c>session_notes</c> row, so the loser's text is appended to the winner's rather than
/// re-keyed (spec 9.7). Ascending. Empty for an unresolved-name loser, which has no
/// notes.</param>
/// <param name="AliasesToAdd">The strings the winner's <c>aliases</c> array would gain, in the
/// order the merge would add them, so the dialog can state them.</param>
public sealed record MergePreview(
    MergePreviewSide Winner,
    MergePreviewSide? Loser,
    int FramesToMove,
    IReadOnlyList<DateOnly> CollidingSessionDates,
    IReadOnlyList<string> AliasesToAdd);

/// <summary>
/// The read behind spec 12.9's merge preview dialog: both sides of the comparison, the frames the
/// merge would move, the session dates whose notes would collide, and the aliases the winner
/// would gain.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Nothing here writes: confirming the merge is <see cref="MergeRepository"/>'s, and
/// this type never opens a transaction.
/// </para>
/// <para>
/// <c>AliasesToAdd</c> is computed by calling <see cref="MergeRepository.AbsorbAliases"/>, the
/// single owner of the case-insensitive union rule (collision map, designated owners). A second
/// implementation would let the dialog promise an alias the merge does not add.
/// </para>
/// </remarks>
public sealed class MergePreviewQuery(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// Spec 12.9's preview. Null when the winner does not exist or is merged away, or when a
    /// named loser target does not exist or is merged away, which the dialog renders as an error
    /// line with confirm disabled, never as an exception on the UI thread.
    /// </summary>
    /// <param name="winnerId">The surviving target.</param>
    /// <param name="loserId">A target loser. Mutually exclusive with
    /// <paramref name="loserName"/>.</param>
    /// <param name="loserName">An unresolved <c>OBJECT</c> string loser, which has no
    /// <c>targets</c> row.</param>
    public MergePreview? Get(Guid winnerId, Guid? loserId, string? loserName)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // Statement 1 and 2. EF binds these operands itself; the hand-written statements below
        // are the ones that need SqlFragments, and they bind through SqlParameters.
        var winner = context.Targets.SingleOrDefault(row => row.Id == winnerId && row.MergedIntoId == null);
        if (winner is null)
        {
            return null;
        }

        Target? loser = null;
        if (loserId is { } id)
        {
            loser = context.Targets.SingleOrDefault(row => row.Id == id && row.MergedIntoId == null);
            if (loser is null)
            {
                return null;
            }
        }

        var aggregates = ReadTargetAggregates(connection, winnerId, loser?.Id);
        var winnerSide = SideOf(winner, aggregates.GetValueOrDefault(winnerId));

        if (loser is not null)
        {
            var loserSide = SideOf(loser, aggregates.GetValueOrDefault(loser.Id));

            // Spec 9.7's incoming set: the loser's primary_name plus every loser alias. The union
            // rule itself stays in MergeRepository.AbsorbAliases.
            var incoming = new List<string> { loser.PrimaryName };
            incoming.AddRange(SqlReaders.ParseAliases(loser.Aliases));

            return new MergePreview(
                winnerSide,
                loserSide,

                // Not loserSide.FrameCount (review finding 1): the side's frame count is
                // LIGHT-only, matching every other aggregate on the page set, but
                // MergeRepository.Merge moves every images row pointing at the loser, and a
                // calibration frame that carried an OBJECT card has a resolved_target_id too. The
                // figure the dialog promises has to be the figure MergeResult.FramesMoved reports.
                ReadFramesToMove(connection, loser.Id),
                ReadCollidingDates(connection, winnerId, loser.Id),
                MergeRepository.AbsorbAliases(winner, incoming).Added);
        }

        if (string.IsNullOrEmpty(loserName))
        {
            // Target detail opened the dialog with only the current target known. There is a
            // winner to render and nothing else to describe yet.
            return new MergePreview(winnerSide, Loser: null, FramesToMove: 0, [], []);
        }

        var nameAggregate = ReadUnresolvedAggregate(connection, loserName);
        var nameSide = new MergePreviewSide(
            TargetId: null,
            loserName,
            CatalogId: null,
            Aliases: [],
            ObjectType: null,

            // The same vocabulary TargetDetailQuery and SearchResultViewModel use for a frame
            // with no resolved target, so all three speak one language.
            TargetListingCriteria.UnresolvedCategory,
            Ra: null,
            Dec: null,
            nameAggregate.FrameCount,
            nameAggregate.IntegrationSeconds,
            nameAggregate.SessionCount,
            nameAggregate.FirstSession,
            nameAggregate.LastSession);

        return new MergePreview(
            winnerSide,
            nameSide,

            // An unresolved name has no session_notes rows to collide: a note is keyed on a
            // target id and this side has none.
            nameSide.FrameCount,
            [],
            MergeRepository.AbsorbAliases(winner, [loserName]).Added);
    }

    /// <summary>
    /// The active target whose <c>primary_name</c> is <paramref name="sourceName"/>, compared
    /// case-insensitively, or null when no target carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruling Q18: which merge shape a <c>merge_candidates</c> row maps to is decided here, in
    /// Data, and never by a string match in a view-model. A Pass 2 candidate's
    /// <c>source_name</c> is another active target's <c>primary_name</c> and maps to that
    /// target's id; anything else is an unresolved <c>OBJECT</c> string and maps to a name loser.
    /// </para>
    /// <para>
    /// Case-insensitive because <c>MergeRepository</c> matches <c>source_name</c> with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> throughout (review escalation ruling): a
    /// case-sensitive lookup here would send a differently-cased Pass 2 row down the
    /// unresolved-name path, which merges no frames and adds a redundant alias.
    /// </para>
    /// </remarks>
    public Guid? ActiveTargetIdByPrimaryName(string sourceName)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return null;
        }

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        var nameParam = parameters.Add(sourceName);

        // Hand-written rather than LINQ because EF cannot express a per-comparison collation here.
        // The operand is still bound; only the collation is literal text.
        //
        // ponytail: SQLite's NOCASE folds ASCII only, where OrdinalIgnoreCase folds Unicode too, so
        // a non-ASCII primary_name differing only in case would still miss. Target names are
        // catalog designations and OBJECT cards, which are ASCII. Upgrade path if that ever
        // changes: read the active names and compare in C# with OrdinalIgnoreCase, the way
        // TargetSearchQuery already reads the full target list.
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id FROM targets
            WHERE merged_into_id IS NULL AND primary_name = {nameParam} COLLATE NOCASE
            LIMIT 1;
            """;
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetGuid(0) : null;
    }

    private static MergePreviewSide SideOf(Target target, Aggregate aggregate) => new(
        target.Id,
        target.PrimaryName,
        target.CatalogId,
        SqlReaders.ParseAliases(target.Aliases),
        target.ObjectType,
        ObjectTypeCategories.Categorize(target.ObjectType),
        target.Ra,
        target.Dec,
        aggregate.FrameCount,
        aggregate.IntegrationSeconds,
        aggregate.SessionCount,
        aggregate.FirstSession,
        aggregate.LastSession);

    // LIGHT only, matching every other aggregate on the page set, so the frame count this dialog
    // states is the frame count Target detail states for the same target. count(DISTINCT
    // session_date) ignores nulls in SQLite, which is spec 12.4's "the number of dated sessions".
    private static Dictionary<Guid, Aggregate> ReadTargetAggregates(
        SqliteConnection connection, Guid winnerId, Guid? loserId)
    {
        var parameters = new SqlParameters();
        var ids = loserId is { } loser
            ? $"{parameters.Add(winnerId)}, {parameters.Add(loser)}"
            : parameters.Add(winnerId);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT i.resolved_target_id, count(*), coalesce(sum(i.exposure_time), 0),
                   count(DISTINCT i.session_date), min(i.session_date), max(i.session_date)
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly} AND i.resolved_target_id IN ({ids})
            GROUP BY i.resolved_target_id;
            """;
        parameters.ApplyTo(command);

        var aggregates = new Dictionary<Guid, Aggregate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            aggregates[reader.GetGuid(0)] = ReadAggregate(reader, 1);
        }

        return aggregates;
    }

    // Scoped with SqlFragments.GroupScope so the count matches what the dashboard shows for that
    // obj: row, and so an unquoted numeric OBJECT card still matches (Phase 5 fix F1). The LIGHT
    // predicate is composed explicitly, because GroupScope never emits one.
    private static Aggregate ReadUnresolvedAggregate(SqliteConnection connection, string loserName)
    {
        var parameters = new SqlParameters();
        var scope = SqlFragments.GroupScope(SqlFragments.UnresolvedKeyPrefix + loserName, parameters.Add);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT count(*), coalesce(sum(i.exposure_time), 0),
                   count(DISTINCT i.session_date), min(i.session_date), max(i.session_date)
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly} AND {scope};
            """;
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAggregate(reader, 0) : new Aggregate(0, 0d, 0, null, null);
    }

    // Every images row pointing at the loser, calibration included, because that is exactly what
    // MergeRepository.Merge moves (review finding 1). Deliberately not filtered by
    // SqlFragments.LightFrameOnly: this figure is a promise about the write, not an aggregate
    // about the target.
    private static int ReadFramesToMove(SqliteConnection connection, Guid loserId)
    {
        var parameters = new SqlParameters();
        var loserParam = parameters.Add(loserId);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM images i WHERE i.resolved_target_id = {loserParam};";
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();
        return reader.Read() ? SqlReaders.ReadInt(reader, 0) : 0;
    }

    // Spec 9.7: on a date the winner already has a note, the loser's text is appended rather than
    // re-keyed. These are exactly those dates.
    private static IReadOnlyList<DateOnly> ReadCollidingDates(
        SqliteConnection connection, Guid winnerId, Guid loserId)
    {
        var parameters = new SqlParameters();
        var winnerParam = parameters.Add(winnerId);
        var loserParam = parameters.Add(loserId);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT w.session_date FROM session_notes w
            JOIN session_notes l ON l.session_date = w.session_date AND l.target_id = {loserParam}
            WHERE w.target_id = {winnerParam}
            ORDER BY w.session_date;
            """;
        parameters.ApplyTo(command);

        var dates = new List<DateOnly>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (SqlReaders.ReadDate(reader, 0) is { } date)
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    private static Aggregate ReadAggregate(SqliteDataReader reader, int offset) => new(
        SqlReaders.ReadInt(reader, offset),
        SqlReaders.ReadDouble(reader, offset + 1),
        SqlReaders.ReadInt(reader, offset + 2),
        SqlReaders.ReadDate(reader, offset + 3),
        SqlReaders.ReadDate(reader, offset + 4));

    private readonly record struct Aggregate(
        int FrameCount,
        double IntegrationSeconds,
        int SessionCount,
        DateOnly? FirstSession,
        DateOnly? LastSession);
}
