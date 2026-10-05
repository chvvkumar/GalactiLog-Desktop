using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GalactiLog.App.ViewModels.Activity;

/// <summary>One line of the key-value rendering: the humanized key, the formatted value, and
/// whether the value is a number, which is what the view right-aligns and sets tabular figures
/// on.</summary>
public sealed record ActivityDetailRow(string Key, string Value, bool IsNumeric);

/// <summary>
/// Spec 12.6's "Details render as a key-value table, falling back to formatted JSON", ported from
/// <c>frontend/src/components/ActivityFeed.tsx</c>'s <c>RowDetails</c>.
/// </summary>
/// <remarks>
/// <para>
/// The web has four renderings; this has two. Its first two cases are the <c>scan</c>
/// <c>failed_files</c> list and the <c>enrichment</c> <c>failed_targets</c> list, and this port
/// emits neither payload today (spec 10.9's table has no such key), so they fold into the general
/// case. When a pass starts writing one, the list rendering belongs here and nowhere else.
/// </para>
/// <para>
/// A details document that is not valid JSON renders as its raw string in the fallback block
/// rather than throwing. One hand-edited or truncated payload must not be able to take the
/// activity page down, which is the same defensive rule <c>SqlReaders.ParseAliases</c> and
/// <c>MergeHistoryQuery</c> follow on the read side.
/// </para>
/// <para>
/// Activity <c>details</c> documents are snake_case (HANDOFF section 4 item 9). This type reads
/// them and never writes one, so nothing here re-cases a key on the way back to the database;
/// <see cref="HumanizeKey"/> is display formatting only.
/// </para>
/// </remarks>
public sealed partial class ActivityDetailsViewModel
{
    private ActivityDetailsViewModel(IReadOnlyList<ActivityDetailRow> rows, string? json)
    {
        Rows = rows;
        Json = json;
    }

    /// <summary>The key-value table, empty when the document fell through to the JSON block.
    /// </summary>
    public IReadOnlyList<ActivityDetailRow> Rows { get; }

    /// <summary>The formatted JSON block, null when the key-value table is showing. Indented two
    /// spaces, which is <c>System.Text.Json</c>'s own default and matches the web's
    /// <c>JSON.stringify(details, null, 2)</c>.</summary>
    public string? Json { get; }

    public bool ShowTable => Rows.Count > 0;

    public bool ShowJson => Json is not null;

    /// <summary>
    /// Builds the rendering for one row's raw <c>details</c> column. Never returns null and never
    /// throws.
    /// </summary>
    public static ActivityDetailsViewModel Create(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return new ActivityDetailsViewModel([], null);
        }

        if (TryParse(details) is not { } document)
        {
            // Not JSON at all: show what is actually stored. Trimmed, because the block is
            // monospace and a trailing newline would render as an empty line.
            return new ActivityDetailsViewModel([], details.Trim());
        }

        using (document)
        {
            var root = document.RootElement;

            // detailsWithoutAction, applied here as well as in the gate below. RowDetails takes the
            // stripped document, not the raw one, so the `action` key is a UI instruction the web
            // renders as a button and never as a detail line (review finding 5). Latent today, since
            // nothing in this port emits the key, and applied anyway so it cannot become a
            // divergence the day something does.
            var properties = WithoutAction(root);

            // The web's isShallowPrimitiveObject: a non-empty object every one of whose values is
            // null, a string, a number or a boolean. Anything else (an array value, a nested
            // object, a top-level array, a bare scalar) is the JSON fallback.
            if (properties is { Count: > 0 } shallow
                && shallow.TrueForAll(property =>
                    property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
            {
                return new ActivityDetailsViewModel(
                    [
                        .. shallow.Select(property => new ActivityDetailRow(
                            HumanizeKey(property.Name),
                            FormatValue(property.Value),
                            property.Value.ValueKind == JsonValueKind.Number)),
                    ],
                    null);
            }

            return new ActivityDetailsViewModel([], Reformat(root, properties));
        }
    }

    /// <summary>
    /// The web's <c>hasDetails()</c> gate, which decides whether a row is expandable at all. Its
    /// point is that half the feed would otherwise grow a chevron over a payload that repeats the
    /// message it sits under.
    /// </summary>
    /// <param name="details">The raw <c>details</c> column.</param>
    /// <param name="message">The row's message, which the redundancy rule reads.</param>
    internal static bool HasRenderableDetails(string? details, string message)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        if (TryParse(details) is not { } document)
        {
            // Unparseable: expandable, because the raw string is exactly what a reader needs to
            // see when a payload is malformed.
            return true;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            // detailsWithoutAction: the `action` key is a UI instruction the web renders as a
            // button, never as a detail line, so it does not make a row expandable on its own.
            var properties = WithoutAction(root)!;

            if (properties.Count == 0)
            {
                return false;
            }

            // detailsRedundantWithMessage: exactly one entry, whose value is a number whose string
            // form already appears in the message. "Orphan rows pruned: 12" carrying {count: 12}
            // is the shape this suppresses.
            //
            // The comparison is against the PARSED number's round-trip form, not the raw JSON text
            // (review finding 7): the web compares String(value), so {"count":12.50} reads "12.5"
            // and {"count":1e3} reads "1000" there. .NET's default double formatting is the
            // shortest round-trippable form, which agrees with JavaScript's for every value this
            // column can carry.
            return properties.Count != 1
                || properties[0].Value.ValueKind != JsonValueKind.Number
                || !message.Contains(NumberText(properties[0].Value), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The web's <c>humanizeKey</c>: underscores and hyphens become spaces, camelCase splits, the
    /// result is trimmed, then the first letter is upper-cased and the rest lower-cased. A key that
    /// is nothing but separators returns unchanged, which is the web's own
    /// <c>if (!spaced) return key</c> (review finding 6).
    /// </summary>
    internal static string HumanizeKey(string key)
    {
        var spaced = CamelBoundary().Replace(Separators().Replace(key, " "), "$1 $2").Trim();
        return spaced.Length == 0
            ? key
            : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    /// <summary>The web's <c>formatValue</c>: null renders "null", a boolean renders "Yes" or
    /// "No", an integer gets thousands separators, a float gets at most four fraction digits, and
    /// anything else renders as its string.</summary>
    internal static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Number => FormatNumber(value),
        JsonValueKind.String => value.GetString() ?? "",
        _ => value.GetRawText(),
    };

    /// <summary>The key the web strips before deciding whether a row is expandable.</summary>
    private const string ActionKey = "action";

    private static string FormatNumber(JsonElement value)
        => value.TryGetInt64(out var whole)
            ? whole.ToString("N0", CultureInfo.InvariantCulture)
            : value.GetDouble().ToString("#,##0.####", CultureInfo.InvariantCulture);

    // JavaScript's String(number): the shortest form that round-trips. .NET's default double
    // formatting has been exactly that since .NET Core 3.0.
    private static string NumberText(JsonElement value)
        => value.GetDouble().ToString(CultureInfo.InvariantCulture);

    /// <summary>The document's properties with the <c>action</c> key removed, or null when the
    /// document is not an object at all. The web's <c>detailsWithoutAction</c>.</summary>
    private static List<JsonProperty>? WithoutAction(JsonElement root)
        => root.ValueKind != JsonValueKind.Object
            ? null
            : [.. root
                .EnumerateObject()
                .Where(property => !string.Equals(property.Name, ActionKey, StringComparison.Ordinal))];

    // The JSON fallback renders the stripped document when there is one, so the two renderings
    // agree about what a details payload contains (review finding 5).
    private static string Reformat(JsonElement root, List<JsonProperty>? properties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            if (properties is null)
            {
                root.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonDocument? TryParse(string details)
    {
        try
        {
            return JsonDocument.Parse(details);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("[_-]+")]
    private static partial Regex Separators();

    // The web's expression verbatim (review finding 6): a lower-case letter followed by an
    // upper-case one, so a key such as scan2Files does NOT split at the digit, exactly as
    // KeyValueDetails.tsx leaves it unsplit.
    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}
