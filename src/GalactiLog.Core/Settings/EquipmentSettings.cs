using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalactiLog.Core.Settings;

// design-spec 5.8.4: equipment is a fixed-shape object with two named keys, each a dictionary
// of canonical name to this shape.
public sealed record EquipmentItemSettings
{
    [JsonPropertyName("aliases")] public string[] Aliases { get; init; } = [];
}

public sealed record EquipmentSettings
{
    [JsonPropertyName("cameras")] public Dictionary<string, EquipmentItemSettings> Cameras { get; init; } = new();
    [JsonPropertyName("telescopes")] public Dictionary<string, EquipmentItemSettings> Telescopes { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
