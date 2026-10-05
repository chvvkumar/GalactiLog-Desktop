using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.Data.Queries;

/// <summary>One frame's raw header document, its provenance document, and the header-derived
/// FWHM that only the raw header panel may display (spec 7.1.1, 12.4).</summary>
/// <param name="RawHeaders">Every header key mapped to its rendered value. A repeated
/// <c>COMMENT</c> or <c>HISTORY</c> card arrives as a list of lines (spec 6.1.2); every other key
/// arrives as a single string. A key whose stored JSON is neither a string nor an array of
/// strings is rendered with <see cref="JsonElement.ToString"/> rather than dropped: this panel
/// exists to show the user what is actually in the file.</param>
/// <param name="Provenance">Stored field name to provenance string (spec 7.3). Only fields that
/// received a value are present.</param>
/// <param name="MedianFwhm">The header <c>MEANFWHM</c> or <c>FWHM</c> value (spec 7.1's
/// median_fwhm row). Never charted, filtered, averaged, or labelled "FWHM".</param>
/// <param name="Fwhm">The CSV <c>fwhm</c> value in arcseconds, so the panel can render both rows
/// adjacently and distinctly without the caller plumbing one of them in from elsewhere.</param>
public sealed record FrameHeaders(
    IReadOnlyList<HeaderEntry> RawHeaders,
    IReadOnlyDictionary<string, string> Provenance,
    double? MedianFwhm,
    double? Fwhm);

/// <param name="Key">The header card name or XISF property id, as stored.</param>
/// <param name="Lines">One entry for a scalar card; several for a repeated <c>COMMENT</c> or
/// <c>HISTORY</c>.</param>
public sealed record HeaderEntry(string Key, IReadOnlyList<string> Lines);

/// <summary>
/// The read behind spec 12.4's raw header panel: one frame's <c>raw_headers</c> and
/// <c>provenance</c> documents plus its two FWHM columns, issued once when a row's panel is first
/// expanded (design lessons rule 1: <c>SessionDetailQuery</c> deliberately omits
/// <c>raw_headers</c> because it is the largest column in the table and only one frame's worth is
/// ever on screen).
/// </summary>
/// <remarks>
/// Read-only over the registered <see cref="DatabaseConnectionString"/>, opened through
/// <see cref="GalactiLogContextOptions.Create"/> so <see cref="PragmaConnectionInterceptor"/>
/// stays in the path, the same shape every other type in this namespace uses. Unlike
/// <c>TargetDetailQuery</c> this query binds one id and needs no <c>SqlFragments.GroupScope</c>
/// composition, so it uses a single <see cref="SqliteParameter"/> directly rather than
/// <c>SqlParameters</c>' bag, matching <c>DistinctHeaderKeysQuery</c>.
/// </remarks>
public sealed class FrameHeadersQuery(DatabaseConnectionString connectionString, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Null when the image row is gone, which happens when a rescan pruned the frame
    /// while the card was open.</summary>
    public FrameHeaders? Get(Guid imageId)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_headers, provenance, median_fwhm, fwhm FROM images WHERE id = @id;";
        command.Parameters.Add(new SqliteParameter("@id", imageId));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var rawHeadersJson = SqlReaders.ReadText(reader, 0);
        var provenanceJson = SqlReaders.ReadText(reader, 1);
        var medianFwhm = SqlReaders.ReadNullableDouble(reader, 2);
        var fwhm = SqlReaders.ReadNullableDouble(reader, 3);

        return new FrameHeaders(
            ParseRawHeaders(rawHeadersJson, imageId),
            ParseProvenance(provenanceJson, imageId),
            medianFwhm,
            fwhm);
    }

    // Rule 1: an array yields one line per element (GetString() when the element is a string,
    // ToString() otherwise); a string yields one line; anything else (a number, a bool, an
    // object) yields one line from ToString() rather than being dropped, because this panel
    // exists to show the user what is actually in the file. Sorted by key, OrdinalIgnoreCase
    // then Ordinal, so the order is total (two keys differing only in case cannot tie).
    private IReadOnlyList<HeaderEntry> ParseRawHeaders(string? json, Guid imageId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var entries = new List<HeaderEntry>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                entries.Add(new HeaderEntry(property.Name, ReadLines(property.Value)));
            }

            return [.. entries
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)];
        }
        catch (JsonException ex)
        {
            // Rule 3: a hand-edited or truncated document must not fail a panel open.
            _logger.LogDebug(ex, "raw_headers for image {ImageId} could not be parsed", imageId);
            return [];
        }
    }

    private static IReadOnlyList<string> ReadLines(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array =>
        [
            .. value.EnumerateArray().Select(element =>
                element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString())
        ],
        JsonValueKind.String => [value.GetString() ?? ""],
        _ => [value.ToString()],
    };

    // Rule 2: a non-string provenance value is rendered with ToString() rather than dropped.
    private Dictionary<string, string> ParseProvenance(string? json, Guid imageId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var map = new Dictionary<string, string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }

            return map;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "provenance for image {ImageId} could not be parsed", imageId);
            return [];
        }
    }
}
