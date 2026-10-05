using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalactiLog.Core.Io;

/// <summary>
/// The stored data location pointer document (spec 17.2). Keys are snake_case, like every other
/// JSON document in this solution, and unknown keys are preserved on write rather than dropped,
/// which is spec 5.8's rule for the settings document applied to this one.
/// </summary>
public sealed record DataRootPointerDocument
{
    /// <summary>The app data root this installation is using.</summary>
    [JsonPropertyName("data_root")]
    public string? DataRoot { get; init; }

    /// <summary>A move the user asked for, performed by the next start.</summary>
    [JsonPropertyName("pending_root")]
    public string? PendingRoot { get; init; }

    /// <summary>The root a completed move copied out of. Nothing deleted it; the Storage tab
    /// names it so the user can remove it themselves.</summary>
    [JsonPropertyName("previous_root")]
    public string? PreviousRoot { get; init; }

    /// <summary>When this document was last written, round-trip format.</summary>
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }

    /// <summary>Anything a later version wrote that this one does not model. Round-trips.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// The data location pointer (spec 17.2). Reads are static and need no <see cref="AppWriter"/>;
/// every write goes through <see cref="AppWriter.WriteDataRootPointer"/>, which is the one
/// authorized writer of this file.
/// </summary>
public sealed class DataRootPointer(AppWriter appWriter)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The pointer file this instance writes.</summary>
    public string Path => appWriter.DataRootPointerPath;

    /// <summary>
    /// The stored document, or null when the file is absent, empty or not valid JSON. Never throws
    /// for a malformed file: a pointer this application cannot read is a pointer that is not
    /// there.
    /// </summary>
    public static DataRootPointerDocument? Read(string pointerPath)
    {
        try
        {
            if (!UserFiles.Exists(pointerPath))
            {
                return null;
            }

            var text = UserFiles.ReadAllText(pointerPath);
            return text.Trim().Length == 0 ? null : JsonSerializer.Deserialize<DataRootPointerDocument>(text);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records a move the user asked for. Writes <c>pending_root</c> and leaves <c>data_root</c>
    /// alone, so a failed or interrupted move never leaves the application pointed at a root that
    /// does not hold the data yet.
    /// </summary>
    public DataRootPointerDocument RequestMove(string destinationRoot)
        => Write(Current() with { PendingRoot = Normalize(destinationRoot) });

    /// <summary>Promotes a completed move: <c>data_root</c> becomes <paramref name="newRoot"/>,
    /// <c>previous_root</c> becomes <paramref name="previousRoot"/>, <c>pending_root</c> is
    /// cleared.</summary>
    public DataRootPointerDocument CompleteMove(string newRoot, string previousRoot)
        => Write(Current() with
        {
            DataRoot = Normalize(newRoot),
            PreviousRoot = Normalize(previousRoot),
            PendingRoot = null,
        });

    /// <summary>Clears <c>pending_root</c> and changes nothing else.</summary>
    public DataRootPointerDocument CancelMove() => Write(Current() with { PendingRoot = null });

    /// <summary>
    /// Writes <c>data_root</c> for a root the application resolved without a pointer, so the next
    /// start reads the pointer rather than the default. Leaves <c>pending_root</c> and
    /// <c>previous_root</c> as they are.
    /// </summary>
    public DataRootPointerDocument RecordRoot(string root)
        => Write(Current() with { DataRoot = Normalize(root) });

    /// <summary>Paths are stored fully qualified with no trailing separator, so two spellings of
    /// one folder compare equal. Null and blank stay as they are.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return value;
        }
    }

    private DataRootPointerDocument Current() => Read(Path) ?? new DataRootPointerDocument();

    private DataRootPointerDocument Write(DataRootPointerDocument document)
    {
        var stamped = document with { UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        appWriter.WriteDataRootPointer(JsonSerializer.Serialize(stamped, WriteOptions));
        return stamped;
    }
}
