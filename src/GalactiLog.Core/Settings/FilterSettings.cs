using System.Text.Json.Serialization;

namespace GalactiLog.Core.Settings;

// design-spec 5.8.4: filters is a dictionary at the JSON-object root, canonical filter name to
// this shape. The dictionary itself preserves arbitrary canonical names; no extension-data
// wrapper is needed at the top level.
public sealed record FilterSetting
{
    // P13 R2a, ruling Q2: no initializer, and nullable. "#808080" as the property's default made
    // "no colour stored" inexpressible -- every document, including one that had never been
    // written, handed AliasMap a grey that outranked the seeded palette, so a category default
    // could never win. Null is what "nobody chose a colour for this filter" now means. A document
    // holding an explicit "#808080" is read as unstored too (Task 3 review P3-5 re-ruled the Q2
    // tail; FilterColor.AsStored is where that rule lives), because that value is indistinguishable
    // from the fallback every pre-P13 build wrote; a user who wants grey picks any grey but it.
    [JsonPropertyName("color")] public string? Color { get; init; }
    [JsonPropertyName("aliases")] public string[] Aliases { get; init; } = [];
}
