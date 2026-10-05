using System.Buffers;
using System.Text.Json;

namespace GalactiLog.Core.Settings;

/// <summary>The stored-document spine the settings readers and writers of design-spec 5.8.1 share:
/// the two total text accessors, which answer null rather than throwing on an unpaired UTF-16
/// escape, and the one replace-or-append write that carries every untouched entry through as its
/// original raw JSON.
/// <para>Extracted at the second occurrence of the pattern, so a keyed settings map has one
/// document shape rather than one per feature; the comparer, the removal arm and the value writer
/// are what the callers still own.</para>
/// <para>Pure: no file system, no clock, no database.</para></summary>
internal static class SettingsDocument
{
    /// <summary>The stored string, or null when it cannot be read.</summary>
    internal static string? TextOf(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The property's name, or null when it cannot be read.</summary>
    internal static string? NameOf(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The stored object with <paramref name="key"/>'s entry replaced, appended or, when
    /// <paramref name="write"/> is null, removed. Every other key is carried through as its stored
    /// text, byte for byte, an existing key keeps its position rather than moving to the end, a
    /// stored value that is not an object starts from an empty one, and a key that cannot be read
    /// is dropped exactly as a reader drops it.
    /// <para><paramref name="write"/> is handed the writer and the key to write under, which is the
    /// stored spelling when an entry is replaced and <paramref name="key"/> when one is
    /// appended.</para></summary>
    internal static JsonElement ReplaceEntry(
        JsonElement? stored,
        string key,
        StringComparison comparison,
        Action<Utf8JsonWriter, string>? write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var replaced = false;

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (stored is { ValueKind: JsonValueKind.Object } document)
            {
                foreach (var property in document.EnumerateObject())
                {
                    if (NameOf(property) is not { } name)
                    {
                        continue;
                    }

                    if (string.Equals(name, key, comparison))
                    {
                        if (write is not null)
                        {
                            write(writer, name);
                            replaced = true;
                        }

                        continue;
                    }

                    // WriteRawValue and not WriteTo: an untouched entry is copied as the stored
                    // text, which is what makes the byte-identity claim above true of the bytes
                    // and not only of the values a re-serialisation would reproduce.
                    writer.WritePropertyName(name);
                    writer.WriteRawValue(property.Value.GetRawText());
                }
            }

            if (!replaced)
            {
                write?.Invoke(writer, key);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }
}
