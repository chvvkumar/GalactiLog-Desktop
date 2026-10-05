using System.Globalization;
using System.Text.Json;
using GalactiLog.Core.Targets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// One row of the dashboard search dropdown (spec 12.2's Search row): either a resolved target or
/// an unresolved <c>OBJECT</c> string, never both.
/// </summary>
/// <param name="TargetId">The target, or null for an unresolved <c>OBJECT</c> result.</param>
/// <param name="UnresolvedObject">The raw <c>OBJECT</c> string, null for a target result. Pinned
/// as <see cref="TargetListingCriteria.UnresolvedObject"/>, which the group key renders as
/// <c>obj:&lt;name&gt;</c>.</param>
/// <param name="DisplayName">The primary name, or the raw <c>OBJECT</c> string.</param>
/// <param name="ObjectType">The raw OTYPELIST, null for an unresolved result.</param>
/// <param name="MatchedOn">The candidate string that produced <paramref name="Score"/>: the
/// primary name, the catalog id, the common name, or one alias. Spec 12.2 wants "the matched
/// alias" shown, so the winner is recorded rather than recomputed by the view.</param>
/// <param name="FrameCount">Frames behind an unresolved result (spec 12.2); 0 for a target.</param>
/// <param name="Score">The <c>similarity()</c> score, at or above <see cref="TargetSearchQuery.Threshold"/>.</param>
public sealed record TargetSearchResult(
    Guid? TargetId,
    string? UnresolvedObject,
    string DisplayName,
    string? ObjectType,
    string? MatchedOn,
    int FrameCount,
    double Score);

/// <summary>
/// The dashboard's fuzzy, alias-aware target search (spec 9.7, 12.2).
/// <para>
/// Scoring runs in C# because <see cref="Trigram.Similarity"/> is the port of PostgreSQL's
/// <c>similarity()</c> and SQLite has no such function. The query reads the candidates, scores
/// them in memory and returns the ranked head. Spec 9.7 is explicit that each of
/// <c>primary_name</c>, <c>catalog_id</c>, <c>common_name</c> and each alias is scored
/// <em>separately</em> and the maximum taken, never a concatenated blob of all of them;
/// <c>word_similarity()</c> is deliberately not ported.
/// </para>
/// </summary>
public sealed class TargetSearchQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Spec 9.7's threshold, the same figure the resolution stack uses.</summary>
    public const double Threshold = 0.4;

    /// <summary>Ranked matches, best first, ties broken by display name so the dropdown does not
    /// reshuffle between identical queries.</summary>
    /// <param name="term">The raw search box text. Trimmed and otherwise passed through:
    /// <see cref="Trigram.Similarity"/> lowercases and pads both sides itself, and
    /// pre-normalizing here would move the scores off the captured PostgreSQL reference values
    /// <c>TrigramTests</c> pins.</param>
    /// <param name="limit">Maximum results, default 20.</param>
    public IReadOnlyList<TargetSearchResult> Search(string term, int limit = 20)
    {
        var trimmed = term?.Trim() ?? "";
        if (trimmed.Length == 0 || limit <= 0)
        {
            // No database round trip for an empty box: this runs per debounced keystroke.
            return [];
        }

        // ponytail: a full read of targets per debounced keystroke. Ceiling is a library with
        // tens of thousands of targets. Upgrade path if it ever matters: cache the candidate
        // list and invalidate it on ScanStatusService.ScanFinished, which already exists.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id, primary_name, catalog_id, common_name, object_type, aliases
            FROM targets
            WHERE merged_into_id IS NULL;

            {SqlFragments.UnresolvedObjectCounts};
            """;

        var results = new List<TargetSearchResult>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var primaryName = reader.GetString(1);
            var (score, matchedOn) = BestScore(
                trimmed,
                [primaryName, SqlReaders.ReadText(reader, 2), SqlReaders.ReadText(reader, 3), .. SqlReaders.ParseAliases(SqlReaders.ReadText(reader, 5))]);

            if (score >= Threshold)
            {
                results.Add(new TargetSearchResult(
                    reader.GetGuid(0),
                    UnresolvedObject: null,
                    primaryName,
                    SqlReaders.ReadText(reader, 4),
                    matchedOn,
                    FrameCount: 0,
                    score));
            }
        }

        reader.NextResult();
        while (reader.Read())
        {
            // json_extract yields a SQLite number for an unquoted numeric OBJECT card, so this
            // reads the value rather than asserting TEXT.
            var name = SqlReaders.ReadText(reader, 0);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var score = Trigram.Similarity(trimmed, name);
            if (score >= Threshold)
            {
                results.Add(new TargetSearchResult(
                    TargetId: null,
                    name,
                    name,
                    ObjectType: null,
                    MatchedOn: name,
                    SqlReaders.ReadInt(reader, 1),
                    score));
            }
        }

        return
        [
            .. results
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.DisplayName, StringComparer.Ordinal)
                .Take(limit),
        ];
    }

    // Spec 9.7: the maximum over the candidates scored one at a time. The first candidate to
    // reach the maximum wins the tie, which puts the primary name ahead of an alias that scores
    // identically -- the name the row displays is then also the name it says it matched.
    private static (double Score, string? MatchedOn) BestScore(string term, IReadOnlyList<string?> candidates)
    {
        var best = 0d;
        string? matchedOn = null;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var score = Trigram.Similarity(term, candidate);
            if (score > best)
            {
                best = score;
                matchedOn = candidate;
            }
        }

        return (best, matchedOn);
    }

}
