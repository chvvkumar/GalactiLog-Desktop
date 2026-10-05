using System.Text.Json;
using static GalactiLog.Core.Settings.SettingsDocument;

namespace GalactiLog.Core.Wbpp;

/// <summary>The lookup half of <see cref="WbppQualityByRig"/>, added here rather than beside the
/// record because a blank key's normalisation and the absent-entry fallback are logic rather than
/// shape (records-report.md).</summary>
public sealed partial record WbppQualityByRig
{
    /// <summary>The stored state of one rig, never null. A null or blank key is normalised to
    /// <see cref="DefaultRigKey"/> first, exactly as <c>wbppQualityStore.storageKey</c> does, and
    /// then <b>looked up</b>, so a user who tunes the filter on a rigless selection reads back what
    /// they wrote; only a key with no entry at all answers
    /// <see cref="WbppQualityState.Default"/>.</summary>
    public WbppQualityState For(string? rigKey)
        => Rigs.TryGetValue(NormaliseKey(rigKey), out var state) ? state : WbppQualityState.Default;

    // A typed With(rigKey, state) member was declared here and is deliberately gone (settings
    // review P2-2). Writing a rig through it meant rebuilding the whole map from the typed model,
    // which silently rewrote every other rig's stored entry and dropped anything this build's map
    // cannot represent. The one door is WbppSettingsRead.WriteQualityForRig, which edits the stored
    // document and carries every untouched rig through as its original raw JSON (design lesson 2:
    // the safe form is the only form there is). Nothing called With, so nothing lost a caller.

    // Ordinal throughout: a rig label is data the catalogue produced, not something the user typed
    // twice (core-shapes.md section 6). Internal because the writer beside this record normalises
    // a key the same way, and a second spelling of the rule is what would let a write and a read
    // address two slots.
    internal static string NormaliseKey(string? rigKey)
        => string.IsNullOrWhiteSpace(rigKey) ? DefaultRigKey : rigKey;
}

/// <summary>The tolerant readers and the writers of design-spec 5.8.1's four Phase 16
/// <c>general</c> keys, ported from <c>frontend/src/lib/wbppQualityStore.ts</c> for the quality map
/// and from the Python's <c>DEFAULT_EXCLUSIONS</c> for the exclusion list.
/// <para>
/// <b>Every reader is total.</b> It answers for any <see cref="JsonElement"/>, including a string,
/// a number, an array where an object was expected, a nested container and a null element, and it
/// throws for none of them. That is what spec 5.8.1's "No stored value of any of the four, whatever
/// a hand edit put there, stops this application starting" means in code:
/// <c>SettingsStore.Deserialize</c> has no catch, so the tolerance cannot live in the deserializer
/// and has to live here.
/// </para>
/// <para>
/// Totality covers one input class beyond a wrong JSON kind, and it is the one a first draft of
/// this file missed (settings review P2-1). A stored string may carry an <b>unpaired UTF-16
/// escape</b>, such as <c>"\ud800"</c>: the JSON grammar permits it, the deserializer accepts it
/// without complaint, and the throw comes later, out of <see cref="JsonElement.GetString()"/> and
/// out of <see cref="JsonProperty.Name"/>, which is to say out of a reader, on whoever opens the
/// export page. Every text this file reads therefore goes through <c>TextOf</c> or <c>NameOf</c>,
/// which answer null instead of throwing, and a null reads as that value's own fallback: an
/// unreadable os literal reads as PowerShell, an unreadable staging path as unset, an unreadable
/// exclusion entry, constraint literal or rig key is dropped, an unreadable baseline as
/// <see cref="QualityBaseline.Session"/>. The dropped rig key is spec 5.8.1's own "A non-string key
/// is dropped" rule, which turns out to be reachable after all.
/// </para>
/// <para>
/// One limit is outside this file and is stated so no reader of it is surprised: a stored value
/// nested deeper than 64 levels throws <see cref="JsonException"/> inside
/// <c>SettingsStore.Deserialize</c>, before any reader here runs, because that method passes a
/// default <c>JsonSerializerOptions</c> and has no catch. It is document wide, predates this phase,
/// applies equally to an unknown key travelling through <c>ExtensionData</c>, and is carried item
/// 41 rather than any of these four keys' business.
/// </para>
/// <para>Pure: no file system, no clock, no database. <c>System.Text.Json</c> and nothing
/// else.</para></summary>
public static class WbppSettingsRead
{
    /// <summary>The stored literal <c>wbpp_default_os</c> falls back to, and the flavour the port
    /// ships.</summary>
    public const string PowerShellOs = "powershell";

    private const string SessionLiteral = "session";
    private const string RigLiteral = "rig";

    private const string HfrLiteral = "hfr";
    private const string EccLiteral = "ecc";
    private const string FwhmLiteral = "fwhm";
    private const string StarsLiteral = "stars";
    private const string RmsLiteral = "rms";
    private const string AtMostLiteral = "lte";
    private const string AtLeastLiteral = "gte";

    private const string EnabledMember = "enabled";
    private const string BaselineMember = "baseline";
    private const string ConstraintsMember = "constraints";
    private const string MetricMember = "metric";
    private const string OpMember = "op";
    private const string ValueMember = "value";

    // The Python's DEFAULT_EXCLUSIONS, verbatim and in its order (spec 5.8.1). One Task 3a case
    // pins this list against ScriptGenerator.DefaultExclusions, which is the generator's own copy,
    // so the two transcriptions cannot drift apart without a red test.
    private static readonly string[] DefaultExclusionList =
    [
        "WBPP",
        "PixInsight",
        "finals",
        "WORK_AREA",
        "masters",
        "Masters",
        "MASTERS",
        "*CALIBRATED",
        "CALIBRATED",
    ];

    // Spec 5.8.1: a pattern reaches both script flavours inside a quoted literal, so a pattern
    // carrying a character the generator refuses is dropped on read as well as refused by the
    // editor. The set has exactly one home, ScriptGenerator.IsRefusedPatternChar, so the reader
    // and the generator cannot drift apart; a second copy here is what let the backslash and the
    // Unicode quote characters be missing from one side.

    /// <summary>The stored <c>wbpp_default_os</c> literal, or <see cref="PowerShellOs"/> when the
    /// key is absent or holds any JSON kind that is not a string. Returns the <b>literal</b> and
    /// never <see cref="WbppScriptType"/>, so this file takes no compile dependency on the script
    /// generator; <c>ScriptGenerator.ParseOs</c> maps it, and reads a literal outside the set as
    /// PowerShell in turn.</summary>
    public static string DefaultOs(JsonElement? stored)
        => stored is { ValueKind: JsonValueKind.String } element
            ? TextOf(element) ?? PowerShellOs
            : PowerShellOs;

    /// <summary>The stored <c>wbpp_staging_path</c>, or null meaning <b>unset</b>. A value that is
    /// not a JSON string, and a string that is empty or whitespace, both read as unset; nothing
    /// derives a staging root from anything else (user ruling 1 at the 12.13 gate). The value is
    /// returned as it was stored, not trimmed: the editor is what normalises a path before it is
    /// written, and a read must not silently alter a destination.</summary>
    public static string? StagingPath(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.String } element)
        {
            return null;
        }

        var value = TextOf(element);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The stored <c>wbpp_exclusions</c> patterns, in order, or the nine defaults when the
    /// document holds anything that is not an array. Inside an array, spec 5.8.1's three drop
    /// rules: an entry that is not a string, an entry empty after trimming, and an entry carrying a
    /// character <see cref="ScriptGenerator.IsRefusedPatternChar"/> refuses are each dropped while
    /// every other entry survives. An array that survives as empty stays empty, because a
    /// user who cleared the list meant it and the scripts then exclude no folder.</summary>
    public static IReadOnlyList<string> Exclusions(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Array } document)
        {
            return DefaultExclusionList;
        }

        var kept = new List<string>();
        foreach (var entry in document.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            var value = TextOf(entry);
            if (string.IsNullOrWhiteSpace(value) || value.Any(ScriptGenerator.IsRefusedPatternChar))
            {
                continue;
            }

            kept.Add(value);
        }

        return kept;
    }

    /// <summary>The stored <c>wbpp_quality_by_rig</c> map, a port of <c>loadWbppQualityState</c>
    /// and <c>parseConstraint</c> minus one nesting level: the port stores the flat
    /// <c>{enabled, baseline, constraints}</c> shape spec 5.8.1 prints, so the word <c>config</c>
    /// appears nowhere here. A reader that required one would reject every entry
    /// <see cref="WriteQualityForRig"/> itself writes, and the user's saved limits would read back
    /// as the default on the next page open.
    /// <para>
    /// Every degradation is local. Anything that is not a JSON object reads as an empty map, so
    /// every rig answers <see cref="WbppQualityState.Default"/>. A <b>malformed entry</b>, which
    /// means exactly two things, a value that is not an object or an <c>enabled</c> member present
    /// and not a boolean, reads as that rig's default state and discards no other rig. A
    /// <c>baseline</c> outside the set reads as <see cref="QualityBaseline.Session"/> and a
    /// <c>constraints</c> member absent or not an array reads as an empty list, <b>neither of which
    /// discards the entry</b>: promoting it to a full default would silently switch a rig's filter
    /// off. One malformed constraint is dropped and the rest are kept, because a document written
    /// by a later build that added a metric must not wipe the other constraints when this build
    /// reads it.
    /// </para></summary>
    public static WbppQualityByRig ReadQualityByRig(JsonElement? stored)
    {
        var rigs = new Dictionary<string, WbppQualityState>(StringComparer.Ordinal);
        if (stored is not { ValueKind: JsonValueKind.Object } document)
        {
            return new WbppQualityByRig(rigs);
        }

        foreach (var property in document.EnumerateObject())
        {
            // Spec 5.8.1's "A non-string key is dropped". A JSON key is always a string, so the
            // rule looked unreachable; it is reached by a key carrying an unpaired UTF-16 escape,
            // which JsonProperty.Name throws on (review P2-1). That one entry goes and the rest of
            // the map stands.
            if (NameOf(property) is not { } rig)
            {
                continue;
            }

            rigs[rig] = ReadState(property.Value);
        }

        return new WbppQualityByRig(rigs);
    }

    /// <summary>The stored <c>wbpp_quality_by_rig</c> document with <b>only</b> the given rig's
    /// sub-object replaced, or added when it was not there. Every other rig is carried through as
    /// its <b>original raw JSON</b>, byte for byte, so an unknown member and a constraint naming a
    /// metric this build does not know both survive a write of a different rig.
    /// <para>
    /// This is the only writer of the key, deliberately (settings review P2-2, design lesson 2).
    /// The member it replaced took the typed map and rebuilt the whole document from it, which
    /// honoured spec 5.8.1's "A write replaces one rig's entry and carries every other entry
    /// through unchanged" only for what this build's model can hold and silently deleted the rest.
    /// The reason spec 5.8.1 gives for keeping them is a document written by a later build that
    /// added a metric, and a write is exactly where that loss would happen, because the read
    /// already keeps them.
    /// </para>
    /// <para>
    /// The key is normalised the way <see cref="WbppQualityByRig.For"/> normalises it, so a write
    /// and a read of a rigless selection address one slot. A stored value that is not a JSON object
    /// starts from an empty object, and a stored rig key that cannot be read is dropped, exactly as
    /// the reader drops it. An existing rig keeps its position in the document rather than moving
    /// to the end, so a write is as small a change to the stored text as it can be.
    /// </para></summary>
    public static JsonElement WriteQualityForRig(JsonElement? stored, string? rigKey, WbppQualityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ReplaceEntry(
            stored,
            WbppQualityByRig.NormaliseKey(rigKey),
            StringComparison.Ordinal,
            (writer, key) => WriteEntry(writer, key, state));
    }

    /// <summary>The document a writer stores for <c>wbpp_default_os</c>: the literal as a JSON
    /// string.</summary>
    public static JsonElement WriteDefaultOs(string literal)
        => JsonSerializer.SerializeToElement(literal);

    /// <summary>The document a writer stores for <c>wbpp_staging_path</c>: the path as a JSON
    /// string, or JSON null for unset. A blank path is written as null, so unset has one stored
    /// form rather than two.</summary>
    public static JsonElement WriteStagingPath(string? value)
        => JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(value) ? null : value);

    /// <summary>The document a writer stores for <c>wbpp_exclusions</c>: the patterns as a JSON
    /// array of strings, in order. The refusal rules are the editor's before this point and
    /// <see cref="Exclusions"/>'s after it.</summary>
    public static JsonElement WriteExclusions(IReadOnlyList<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToElement(value);
    }

    /// <summary>The baseline a stored literal names, ordinal and <b>case sensitive</b> against
    /// <c>session</c> and <c>rig</c>. Anything else, including <c>RIG</c>, an unreadable string and
    /// null, reads as <see cref="QualityBaseline.Session"/>.
    /// <para>
    /// Case sensitive by coordinator ruling at the settings review, against this task's own brief,
    /// which directed <c>FrameListFormats.Parse</c>'s ignore-case form: the web's test is
    /// <c>cfg.baseline === "session" || cfg.baseline === "rig"</c>, and spec 5.8.1's "anything else
    /// reads as <c>session</c>" reads that way literally. It now agrees with <c>ParseMetric</c> and
    /// <c>ParseOp</c>, so no stored literal in this file is read two ways.
    /// </para></summary>
    public static QualityBaseline ParseBaseline(string? stored)
        => string.Equals(stored, RigLiteral, StringComparison.Ordinal)
            ? QualityBaseline.Rig
            : QualityBaseline.Session;

    /// <summary>The literal spec 5.8.1 stores for the given baseline.</summary>
    public static string ToStored(QualityBaseline baseline)
        => baseline == QualityBaseline.Rig ? RigLiteral : SessionLiteral;

    // One rig's entry in the flat stored shape spec 5.8.1 prints.
    private static void WriteEntry(Utf8JsonWriter writer, string rig, WbppQualityState state)
    {
        writer.WritePropertyName(rig);
        writer.WriteStartObject();
        writer.WriteBoolean(EnabledMember, state.Enabled);
        writer.WriteString(BaselineMember, ToStored(state.Baseline));
        writer.WriteStartArray(ConstraintsMember);
        foreach (var constraint in state.Constraints)
        {
            writer.WriteStartObject();
            writer.WriteString(MetricMember, MetricLiteral(constraint.Metric));
            writer.WriteString(OpMember, OpLiteral(constraint.Op));
            if (constraint.Value is { } value)
            {
                writer.WriteNumber(ValueMember, value);
            }
            else
            {
                writer.WriteNull(ValueMember);
            }

            writer.WriteBoolean(EnabledMember, constraint.Enabled);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static WbppQualityState ReadState(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object)
        {
            return WbppQualityState.Default;
        }

        // Absent is not malformed: the entry keeps its other members and the filter reads off,
        // which is the default enablement anyway.
        var enabled = false;
        if (value.TryGetProperty(EnabledMember, out var enabledElement))
        {
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return WbppQualityState.Default;
            }

            enabled = enabledElement.ValueKind is JsonValueKind.True;
        }

        var baseline = value.TryGetProperty(BaselineMember, out var baselineElement)
            && baselineElement.ValueKind is JsonValueKind.String
                ? ParseBaseline(TextOf(baselineElement))
                : QualityBaseline.Session;

        var constraints = new List<RawConstraint>();
        if (value.TryGetProperty(ConstraintsMember, out var constraintsElement)
            && constraintsElement.ValueKind is JsonValueKind.Array)
        {
            foreach (var raw in constraintsElement.EnumerateArray())
            {
                if (ReadConstraint(raw) is { } constraint)
                {
                    constraints.Add(constraint);
                }
            }
        }

        return new WbppQualityState(enabled, baseline, constraints);
    }

    // Port of parseConstraint. Every one of the four members has to be present and well formed,
    // exactly as the web's typeof tests require: an absent member is undefined there, which is
    // neither a metric key, nor an op, nor null, nor a boolean. A null value alone is legal, and
    // means a chip the user added and has not filled in, which gates nothing.
    private static RawConstraint? ReadConstraint(JsonElement raw)
    {
        if (raw.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (!raw.TryGetProperty(MetricMember, out var metricElement)
            || metricElement.ValueKind is not JsonValueKind.String
            || ParseMetric(TextOf(metricElement)) is not { } metric)
        {
            return null;
        }

        if (!raw.TryGetProperty(OpMember, out var opElement)
            || opElement.ValueKind is not JsonValueKind.String
            || ParseOp(TextOf(opElement)) is not { } op)
        {
            return null;
        }

        if (!raw.TryGetProperty(ValueMember, out var valueElement))
        {
            return null;
        }

        double? value = null;
        if (valueElement.ValueKind is JsonValueKind.Number)
        {
            // TryGetDouble already answers false for every JSON number that overflows a double,
            // and the grammar carries no infinity or NaN literal, so the IsFinite term cannot fire
            // here. It is kept as the mirror of the web's Number.isFinite test, where it can fire:
            // dropping it reads the same today and stops mirroring the source the day either
            // grammar gains such a literal.
            if (!valueElement.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                return null;
            }

            value = number;
        }
        else if (valueElement.ValueKind is not JsonValueKind.Null)
        {
            return null;
        }

        if (!raw.TryGetProperty(EnabledMember, out var enabledElement)
            || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return new RawConstraint(metric, op, value, enabledElement.ValueKind is JsonValueKind.True);
    }

    // Ordinal and exact, unlike ParseBaseline above, because the web's own membership test is
    // `c.metric in METRIC_DEFS` and `c.op !== "lte" && c.op !== "gte"`, both case sensitive, and an
    // unrecognised literal drops the one constraint rather than reading as a default.
    private static WbppMetric? ParseMetric(string? stored) => stored switch
    {
        HfrLiteral => WbppMetric.Hfr,
        EccLiteral => WbppMetric.Ecc,
        FwhmLiteral => WbppMetric.Fwhm,
        StarsLiteral => WbppMetric.Stars,
        RmsLiteral => WbppMetric.Rms,
        _ => null,
    };

    private static ConstraintOp? ParseOp(string? stored) => stored switch
    {
        AtMostLiteral => ConstraintOp.AtMost,
        AtLeastLiteral => ConstraintOp.AtLeast,
        _ => null,
    };

    private static string MetricLiteral(WbppMetric metric) => metric switch
    {
        WbppMetric.Ecc => EccLiteral,
        WbppMetric.Fwhm => FwhmLiteral,
        WbppMetric.Stars => StarsLiteral,
        WbppMetric.Rms => RmsLiteral,
        _ => HfrLiteral,
    };

    private static string OpLiteral(ConstraintOp op)
        => op == ConstraintOp.AtLeast ? AtLeastLiteral : AtMostLiteral;
}
