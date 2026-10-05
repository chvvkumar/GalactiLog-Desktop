using System.Buffers;
using System.Text.Json;
using static GalactiLog.Core.Settings.SettingsDocument;

namespace GalactiLog.Core.Integrations;

/// <summary>The tolerant readers and the writers of design-spec 5.8.1's four Phase 21
/// <c>general</c> keys: <c>astrobin_filter_ids</c>, <c>astrobin_bortle</c>, <c>nina_instances</c>
/// and <c>stellarium_instances</c>.
/// <para>
/// <b>Every reader is total</b>, in the sense <c>WbppSettingsRead</c>'s header paragraph defines
/// and for the same reason: <c>SettingsStore.Deserialize</c> has no catch, so a stored string
/// carrying an unpaired UTF-16 escape such as <c>"\ud800"</c> would otherwise throw out of
/// <see cref="JsonElement.GetString()"/> or <see cref="JsonProperty.Name"/> on whoever opens the
/// screen. Every text here goes through a helper that answers null rather than throwing, and that
/// one entry is dropped.
/// </para>
/// <para>Pure: no file system, no clock, no database.</para></summary>
public static class IntegrationSettings
{
    private const string NameMember = "name";
    private const string UrlMember = "url";
    private const string EnabledMember = "enabled";

    /// <summary>The lowest and highest Bortle class spec 5.8.1 stores.</summary>
    private const int LowestBortle = 1;
    private const int HighestBortle = 9;

    /// <summary>The stored <c>astrobin_filter_ids</c> map, keyed by a filter name exactly as the
    /// Filters tab spells it. A stored value that is not a positive integer is dropped rather than
    /// clamped, because an AstroBin id is an identity and not a magnitude and a clamp would write
    /// a different filter into the reader's upload (spec 5.8.1 amendment 1b). A non-object reads
    /// as an empty map, and a key that cannot be read is dropped.
    /// <para>Case-insensitive, matching how <c>FilterNameUnion</c> de-duplicates the filter names
    /// the tab offers: two spellings that differ only in case are one filter here, so a later
    /// duplicate key wins rather than throwing.</para></summary>
    public static IReadOnlyDictionary<string, int> ReadFilterIds(JsonElement? stored)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (stored is not { ValueKind: JsonValueKind.Object } document)
        {
            return ids;
        }

        foreach (var property in document.EnumerateObject())
        {
            if (NameOf(property) is not { } name)
            {
                continue;
            }

            if (property.Value.ValueKind is JsonValueKind.Number
                && property.Value.TryGetInt32(out var id)
                && id > 0)
            {
                ids[name] = id;
            }
        }

        return ids;
    }

    /// <summary>The stored <c>astrobin_filter_ids</c> document with only
    /// <paramref name="filterName"/>'s entry replaced, added, or, when <paramref name="id"/> is
    /// null, <b>removed</b>: a box cleared on the External Tools tab removes the key rather than
    /// storing <c>0</c> (spec 5.8.1). Every other key is carried through as its original raw JSON,
    /// byte for byte, so a key a later build added survives a write of a different filter. This is
    /// the only writer of the key.
    /// <para>An existing key keeps its position in the document rather than moving to the end, and
    /// a stored value that is not a JSON object starts from an empty object.</para></summary>
    public static JsonElement WriteFilterId(JsonElement? stored, string filterName, int? id)
    {
        ArgumentNullException.ThrowIfNull(filterName);
        Action<Utf8JsonWriter, string>? write = null;
        if (id is { } value)
        {
            write = (writer, key) => writer.WriteNumber(key, value);
        }

        return ReplaceEntry(stored, filterName, StringComparison.OrdinalIgnoreCase, write);
    }

    /// <summary>The stored <c>astrobin_bortle</c>, clamped to the nearer of 1 and 9, which is the
    /// treatment every other bounded scalar in spec 5.8.1 gets. Null passes through and means
    /// unset, which leaves the CSV cell blank.</summary>
    public static int? ReadBortle(int? stored)
        => stored is { } value ? Math.Clamp(value, LowestBortle, HighestBortle) : null;

    /// <summary>The stored <c>nina_instances</c> or <c>stellarium_instances</c> array, in stored
    /// order, which is the order the tab lists and the menu offers. A non-array reads as empty and
    /// an entry that is not an object is dropped; <c>name</c> and <c>url</c> absent, unreadable or
    /// not a string read as <c>""</c> and <c>enabled</c> absent or not a bool reads as false.
    /// <b>Nothing is dropped for being blank or unparseable</b>: spec 5.8.1 keeps such a row so the
    /// tab can show it and the reader can repair it.</summary>
    public static IReadOnlyList<IntegrationInstance> ReadInstances(JsonElement? stored)
    {
        var instances = new List<IntegrationInstance>();
        if (stored is not { ValueKind: JsonValueKind.Array } document)
        {
            return instances;
        }

        foreach (var entry in document.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            instances.Add(new IntegrationInstance(
                TextMember(entry, NameMember),
                TextMember(entry, UrlMember),
                entry.TryGetProperty(EnabledMember, out var enabled)
                    && enabled.ValueKind is JsonValueKind.True));
        }

        return instances;
    }

    /// <summary>The document a writer stores for either instance list: the whole array, in order.
    /// The whole array rather than one entry, because the tab owns every row of it and the entry
    /// shape has no member a later build could add that this one must preserve.</summary>
    public static JsonElement WriteInstances(IReadOnlyList<IntegrationInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var instance in instances)
            {
                writer.WriteStartObject();
                writer.WriteString(NameMember, instance.Name);
                writer.WriteString(UrlMember, instance.Url);
                writer.WriteBoolean(EnabledMember, instance.Enabled);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static string TextMember(JsonElement entry, string member)
        => entry.TryGetProperty(member, out var value) && value.ValueKind is JsonValueKind.String
            ? TextOf(value) ?? ""
            : "";
}
