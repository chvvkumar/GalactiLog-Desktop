using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The value reads every hand-written query in this namespace needs, in one place.
/// </summary>
/// <remarks>
/// <para>
/// SQLite gives a column whatever storage class its writer used, and an aggregate whatever
/// storage class its inputs had (a sum over an all-null column coalesced to 0 comes back as
/// INTEGER, <c>detected_stars</c> is INTEGER, <c>arcsec_per_pixel</c> REAL, <c>name_locked</c>
/// INTEGER), so every numeric read converts rather than asserting a CLR type.
/// </para>
/// <para>
/// Extracted from <c>TargetListingQuery</c>, <c>TargetSearchQuery</c> and
/// <c>TargetDetailQuery</c>, which held three verbatim copies (Task 1 review finding 1). Any new
/// query in this namespace reads through here rather than adding a fourth.
/// </para>
/// </remarks>
internal static class SqlReaders
{
    /// <summary>The one date format every <c>session_date</c> column is stored and bound in.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    public static string? ReadText(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal).ToString();

    public static double ReadDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0d : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static int ReadInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>For the columns where absent and zero are different answers
    /// (<c>detected_stars</c>, <c>adu_min</c>, <c>camera_gain</c>, <c>focuser_position</c>): a
    /// frame with no star count did not detect zero stars.</summary>
    public static int? ReadNullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A nullable <c>capture_date</c>. Microsoft.Data.Sqlite parses the TEXT storage
    /// form itself, so this never hand-parses a timestamp format.</summary>
    public static DateTime? ReadNullableDateTime(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    public static bool ReadBool(SqliteDataReader reader, int ordinal)
        => !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>session_date</c>. Spec 5.1 stores the column as an invariant
    /// <see cref="DateFormat"/> string and every writer in the application binds it that way, but
    /// the column carries no format constraint, so a hand-edited or imported row can hold
    /// <c>''</c>, a timestamp or a number. Such a value answers null, which is the answer every
    /// caller already handles for an absent date, rather than throwing a
    /// <see cref="FormatException"/> out of the whole read: one row of a library must not empty a
    /// page (review P2-3). Read through <see cref="ReadText"/> so a non-TEXT storage class fails
    /// the parse instead of the cast.</summary>
    public static DateOnly? ReadDate(SqliteDataReader reader, int ordinal)
        => DateOnly.TryParseExact(
            ReadText(reader, ordinal),
            DateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    /// <summary>Deserializes a <c>targets.aliases</c> document. A hand-edited or legacy document
    /// yields an empty list rather than failing the read it was part of.</summary>
    public static IReadOnlyList<string> ParseAliases(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
