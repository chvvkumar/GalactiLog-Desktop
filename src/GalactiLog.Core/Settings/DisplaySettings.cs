using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalactiLog.Core.Settings;

// One metric group's enabled flag and per-field visibility (design-spec 5.8.2).
public sealed record MetricGroupSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("fields")] Dictionary<string, bool> Fields);

/// <summary>
/// Spec 12.4's Target detail page choices, persisted per profile. One object,
/// because separate top-level keys on the display document would be unrelated strings a reader
/// has to know belong together.
/// </summary>
/// <remarks>
/// The record has its own <c>[JsonExtensionData]</c> for the same reason
/// <see cref="DisplaySettings"/> has one: spec 5.8's rule is that an unrecognized key survives a
/// write, and a key retired by a later phase, such as <c>guiding_expanded</c> or the five page
/// arrangement keys (<c>night_detail_expanded</c>, <c>frames_expanded</c>, <c>ledger_expanded</c>,
/// <c>metrics_expanded</c>, <c>night_detail_height</c>) or the page layout key (<c>layout</c>), is carried the same way.
/// </remarks>
/// <remarks>
/// The four keys are spec 5.8.2's grading and frame-list choices, stored as the literal
/// strings that table lists rather than as enums: spec 5.8.2's closing sentence says a stored
/// value outside a key's listed set reads as that key's default rather than throwing, which is a
/// parse at the seam that reads the key, not a converter that would have to throw or guess here.
/// They need no migration: a missing key reads as its default and is written on the first write.
/// </remarks>
public sealed record TargetPageSettings
{
    /// <summary>Spec 12.4's "Compare to" choice, <c>session</c> or <c>rig</c>.</summary>
    [JsonPropertyName("grading_baseline")] public string GradingBaseline { get; init; } = "session";

    /// <summary>The Copy Frame List dialog's output format, <c>explorer</c>, <c>names</c> or
    /// <c>paths</c>.</summary>
    [JsonPropertyName("frame_list_format")] public string FrameListFormat { get; init; } = "paths";

    /// <summary>The Copy Frame List dialog's mode, <c>good</c> or <c>bad</c>.</summary>
    [JsonPropertyName("frame_list_mode")] public string FrameListMode { get; init; } = "good";

    /// <summary>Good mode only: whether frames the grading left ungraded join the list.</summary>
    [JsonPropertyName("frame_list_include_unmeasured")] public bool FrameListIncludeUnmeasured { get; init; } = true;

    /// <summary>Per layout state, keyed by the layout registry's key.</summary>
    [JsonPropertyName("layouts")] public Dictionary<string, TargetLayoutState> Layouts { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    // A dictionary compares by reference, so the record's own equality would call two equal
    // documents different; the layouts are compared entry by entry.
    public bool Equals(TargetPageSettings? other)
        => other is not null
            && GradingBaseline == other.GradingBaseline
            && FrameListFormat == other.FrameListFormat
            && FrameListMode == other.FrameListMode
            && FrameListIncludeUnmeasured == other.FrameListIncludeUnmeasured
            && Layouts.Count == other.Layouts.Count
            && Layouts.All(entry => other.Layouts.TryGetValue(entry.Key, out var theirs) && Equals(entry.Value, theirs))
            && Equals(ExtensionData, other.ExtensionData);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GradingBaseline);
        hash.Add(FrameListFormat);
        hash.Add(FrameListMode);
        hash.Add(FrameListIncludeUnmeasured);
        hash.Add(Layouts.Count);
        return hash.ToHashCode();
    }
}

/// <summary>One layout's stored state under <see cref="TargetPageSettings.Layouts"/>.</summary>
public sealed record TargetLayoutState
{
    /// <summary>The night lanes region's height the user dragged to; null is the automatic rule.</summary>
    [JsonPropertyName("lanes_height")] public double? LanesHeight { get; init; }

    /// <summary>The session chart's own height from the retired second handle (Phase 24 R31). No
    /// longer read: the lanes handle sizes the chart since the night pane round; kept so a stored
    /// document round-trips.</summary>
    [JsonPropertyName("chart_height")] public double? ChartHeight { get; init; }

    /// <summary>The nights sidebar's last open width; null is the wide form at the ledger's own width.</summary>
    [JsonPropertyName("sidebar_width")] public double? SidebarWidth { get; init; }

    [JsonPropertyName("sidebar_collapsed")] public bool SidebarCollapsed { get; init; }

    /// <summary>The Session metrics section's disclosure; null is open.</summary>
    [JsonPropertyName("night_metrics_open")] public bool? NightMetricsOpen { get; init; }

    /// <summary>The retired Session notes section's disclosure. No longer read: the notes are in
    /// the Details drawer since the night pane round; kept so a stored document round-trips.</summary>
    [JsonPropertyName("notes_open")] public bool? NotesOpen { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Spec 12.14's Analysis page choices, persisted per profile (spec 5.8.2's <c>analysis</c>
/// object). Four keys and no others, in one object for the reason
/// <see cref="TargetPageSettings"/> is one object.
/// </summary>
/// <remarks>
/// <para>
/// Four plain strings, exactly as <c>target_page</c>'s four choice keys are stored: spec 5.8.2's
/// closing sentence says a stored value outside a key's listed set reads as that key's default
/// rather than throwing, which is a parse at the seam that reads the key, not a converter that
/// would have to throw or guess here. The four parses live in
/// <c>GalactiLog.App.ViewModels.Analysis.AnalysisDisplay</c>.
/// </para>
/// <para>
/// No migration, for the reason the rest of the document needs none: a document carrying no
/// <c>analysis</c> key at all takes every one of the defaults below, not only the ones it is
/// missing, and gains the keys on its first write. The record carries its own
/// <c>[JsonExtensionData]</c> for the same reason <see cref="TargetPageSettings"/> does: spec
/// 5.8's rule is that an unrecognized key survives a write, inside a nested object too.
/// </para>
/// <para>
/// Spec 12.14's Persistence subsection lists what is deliberately per visit and is NOT here: the
/// Correlation outlier toggle, the Distributions mode, metric and grouping, the Time Series metric
/// and smoothing, the Compare mode, metric and two groups, the equipment combination, the filter
/// selection and the date range. A date range is a question, not a preference.
/// </para>
/// </remarks>
[JsonConverter(typeof(AnalysisDisplaySettingsConverter))]
public sealed record AnalysisDisplaySettings
{
    /// <summary>The selected tab, one of <c>correlation</c>, <c>distributions</c>,
    /// <c>timeseries</c>, <c>matrix</c> and <c>compare</c>. Anything else reads as
    /// <c>correlation</c>.</summary>
    [JsonPropertyName("tab")] public string Tab { get; init; } = "correlation";

    /// <summary>The Correlation tab's X metric key. A key outside the ten X metrics and the five
    /// PHD2 X metrics reads as <c>humidity</c>.</summary>
    [JsonPropertyName("x_metric")] public string XMetric { get; init; } = "humidity";

    /// <summary>The Correlation tab's Y metric key. A key outside the ten Y metrics reads as
    /// <c>hfr</c>, a PHD2 key included: spec 5.8.2 calls that one out, because a PHD2 key here is
    /// a value the Correlation tab would reject at query time.</summary>
    [JsonPropertyName("y_metric")] public string YMetric { get; init; } = "hfr";

    /// <summary>The filter bar's granularity segment, <c>frame</c> or <c>session</c>. Anything
    /// else, case ignored, reads as <c>frame</c>.</summary>
    [JsonPropertyName("granularity")] public string Granularity { get; init; } = "frame";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Reads spec 5.8.2's <c>analysis</c> object, answering each key's default for a token of the
/// wrong kind rather than throwing.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.8.2: nothing here throws on any input, a hand-edited document included.
/// The tolerance is built here, at the one seam that reads the object, rather than assumed of
/// <c>System.Text.Json</c>, which throws on a number, a boolean, an object or an array against a
/// <see cref="string"/> property and assigns null for a null.
/// </para>
/// <para>
/// <see cref="HandleNull"/> is true so that <c>"analysis": null</c> reaches
/// <see cref="Read"/> and answers the defaults; without it the serializer assigns null to a
/// property the whole page then dereferences. The four values are accepted only as JSON strings;
/// every other token is skipped whole and leaves that key at its default. An unrecognized key is
/// carried through <see cref="AnalysisDisplaySettings.ExtensionData"/> and written back, which is
/// spec 5.8's rule and what the record's own <c>[JsonExtensionData]</c> would have done.
/// </para>
/// <para>
/// The keys of <c>target_page</c> and <c>dashboard</c> are covered instead by <c>SettingsStore.ReadDisplay</c>, which drops any member of the stored document this
/// build cannot read and leaves every other member standing. That guard also stands behind this
/// converter, and this converter is still worth its own code: it answers a wrong-kind key without
/// the document being re-read, and it is the only thing that gives <c>"analysis": null</c> the four
/// defaults instead of a null property.
/// </para>
/// </remarks>
public sealed class AnalysisDisplaySettingsConverter : JsonConverter<AnalysisDisplaySettings>
{
    /// <summary>True so that a stored <c>null</c> reaches <see cref="Read"/> and answers the
    /// defaults, rather than the serializer assigning null to the property.</summary>
    public override bool HandleNull => true;

    /// <summary>The stored object, or the four defaults for a token that is not an object. A key
    /// whose value is not a JSON string keeps that key's default and the rest of the object is
    /// still read.</summary>
    public override AnalysisDisplaySettings Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = new AnalysisDisplaySettings();

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            // A string, a number, a boolean, null or an array where the object should be. A
            // container token has to be consumed whole or the document's remaining keys are read
            // against the wrong position; Skip does nothing for the other four.
            reader.Skip();
            return value;
        }

        Dictionary<string, JsonElement>? extension = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString() ?? string.Empty;
            reader.Read();

            switch (name)
            {
                case "tab":
                    value = value with { Tab = ReadString(ref reader, value.Tab) };
                    break;
                case "x_metric":
                    value = value with { XMetric = ReadString(ref reader, value.XMetric) };
                    break;
                case "y_metric":
                    value = value with { YMetric = ReadString(ref reader, value.YMetric) };
                    break;
                case "granularity":
                    value = value with { Granularity = ReadString(ref reader, value.Granularity) };
                    break;
                default:
                    extension ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    extension[name] = JsonElement.ParseValue(ref reader);
                    break;
            }
        }

        value.ExtensionData = extension;
        return value;
    }

    /// <summary>Writes the four keys in spec 5.8.2's own order, then every key the document
    /// carried that this build does not know.</summary>
    public override void Write(
        Utf8JsonWriter writer, AnalysisDisplaySettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("tab", value.Tab);
        writer.WriteString("x_metric", value.XMetric);
        writer.WriteString("y_metric", value.YMetric);
        writer.WriteString("granularity", value.Granularity);

        if (value.ExtensionData is { } extra)
        {
            foreach (var entry in extra)
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static string ReadString(ref Utf8JsonReader reader, string fallback)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? fallback;
        }

        reader.Skip();
        return fallback;
    }
}

/// <summary>
/// Spec 12.2's Dashboard filter panel, persisted per profile (Phase 14C). Two
/// keys, one object, for the reason <see cref="TargetPageSettings"/> is one object: two unrelated
/// top-level strings would be two keys a reader has to know belong together.
/// </summary>
/// <remarks>
/// <para>
/// This is a different persistence tier from the filter values themselves, which live for the
/// running session only (spec 12.2's Layout paragraph). Only the panel's state and its committed
/// width are written here.
/// </para>
/// <para>
/// The read clamp is <see cref="ClampedFilterPanelWidth"/>, a member on this record rather than a
/// line in whichever view-model happens to read the key, so the second reader cannot forget it
/// (design lesson 2: enforce at the one choke point). The write clamp calls the same
/// <see cref="Math.Clamp(int, int, int)"/> through the same three constants. No migration: a
/// document carrying no <c>dashboard</c> key takes both defaults, which is rule 5.8's own opening.
/// </para>
/// </remarks>
public sealed record DashboardDisplaySettings
{
    /// <summary>Spec 12.2's narrowest expanded panel, and the GridSplitter's own lower bound.</summary>
    public const int MinPanelWidth = 220;

    /// <summary>Spec 12.2's widest expanded panel.</summary>
    public const int MaxPanelWidth = 480;

    /// <summary>Spec 5.8.2's default, inside the range above.</summary>
    public const int DefaultPanelWidth = 300;

    /// <summary>Collapsed to its strip on a fresh profile, so it carries no initializer.</summary>
    [JsonPropertyName("filter_panel_expanded")] public bool FilterPanelExpanded { get; init; }

    [JsonPropertyName("filter_panel_width")] public int FilterPanelWidth { get; init; } = DefaultPanelWidth;

    /// <summary>The stored width brought inside 220 to 480, so a hand-edited document cannot
    /// desynchronize the figure from the splitter's own drag range (spec 5.8.2).</summary>
    [JsonIgnore]
    public int ClampedFilterPanelWidth => Math.Clamp(FilterPanelWidth, MinPanelWidth, MaxPanelWidth);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Spec 12.4's "Compare to" baseline: the night's own frames, or every frame captured with the
/// same telescope, camera and filter across the library. It lives beside the document rather than
/// in the App project so a later CLI reader and Task 4's frame-list formats can name it without
/// referencing Avalonia.
/// </summary>
public enum GradingBaseline
{
    /// <summary>The selected night's own frames. Spec 5.8.2's default for
    /// <c>grading_baseline</c>, and the value a stored string outside the listed set reads as.</summary>
    Session,

    /// <summary>Every frame of the same (telescope, camera, filter) group across the library,
    /// served from <c>RigBaselinesCache</c>.</summary>
    Rig,
}

// design-spec 5.8.2. Defaults are schemas/settings.py::default_display_settings verbatim:
// Quality and Guiding enabled, the other four disabled, every field within each group true.
public sealed record DisplaySettings
{
    [JsonPropertyName("groups")]
    public Dictionary<string, MetricGroupSettings> Groups { get; init; } = new()
    {
        ["quality"] = new MetricGroupSettings(true, new()
        {
            ["hfr"] = true, ["hfr_stdev"] = true, ["fwhm"] = true, ["eccentricity"] = true, ["detected_stars"] = true,
        }),
        ["guiding"] = new MetricGroupSettings(true, new()
        {
            ["rms_total"] = true, ["rms_ra"] = true, ["rms_dec"] = true,
        }),
        ["adu"] = new MetricGroupSettings(false, new()
        {
            ["mean"] = true, ["median"] = true, ["stdev"] = true, ["min"] = true, ["max"] = true,
        }),
        ["focuser"] = new MetricGroupSettings(false, new()
        {
            ["position"] = true, ["temp"] = true,
        }),
        ["weather"] = new MetricGroupSettings(false, new()
        {
            ["ambient_temp"] = true, ["dew_point"] = true, ["humidity"] = true, ["pressure"] = true,
            ["wind_speed"] = true, ["wind_direction"] = true, ["wind_gust"] = true, ["cloud_cover"] = true,
            ["sky_quality"] = true,
        }),
        ["mount"] = new MetricGroupSettings(false, new()
        {
            ["airmass"] = true, ["pier_side"] = true, ["rotator_position"] = true,
        }),
    };

    /// <summary>The table id of the dashboard target list (design-spec 12.2).</summary>
    public const string DashboardTableId = "dashboard";

    /// <summary>The table id of the Target detail frame table (design-spec 12.4).</summary>
    public const string FramesTableId = "frames";

    /// <summary>The table id of the Target detail page's Nights ledger (spec 12.4, 12.15).
    /// Unlike the other two ids, this list holds CUSTOM SLUGS ONLY: the ledger's built-in
    /// columns are not hideable and are not in it (spec 5.8.2's Phase 20 note). No longer read:
    /// custom column visibility is <see cref="LedgerHiddenTableId"/>'s; kept so a stored document
    /// round-trips.</summary>
    public const string LedgerTableId = "ledger";

    /// <summary>The custom slugs the user switched off on the dashboard. A <c>columns</c> entry
    /// that lists HIDDEN keys, unlike every other entry: a custom column is shown unless its slug
    /// is here, so a column created later starts shown with no write. Absent on a fresh profile,
    /// which <see cref="ColumnsFor"/> answers as empty.</summary>
    public const string DashboardHiddenTableId = "dashboard_hidden";

    /// <summary>The custom slugs the user switched off on the Nights ledger, the same shape as
    /// <see cref="DashboardHiddenTableId"/>.</summary>
    public const string LedgerHiddenTableId = "ledger_hidden";

    /// <summary>The table id of the Mosaics page's table (spec 12.17, 5.8.2, Phase 18).</summary>
    public const string MosaicsTableId = "mosaics";

    /// <summary>Spec 5.8.2's five built-in keys of the mosaics table, in order. <c>name</c> is
    /// locked on; the five are also the only keys <see cref="MosaicsSort"/> accepts.</summary>
    public static readonly ImmutableArray<string> MosaicColumnKeys = ["name", "panels", "integration", "frames", "date_range"];

    /// <summary>design-spec 5.8.2's per-table default visible column lists, in order. The one
    /// place the defaults exist: <see cref="Columns"/>'s initializer is built from this, and
    /// <see cref="ColumnsFor"/> falls back to it. Phase 6's frame table reads the same table.</summary>
    public static readonly IReadOnlyDictionary<string, ImmutableArray<string>> DefaultColumns = new Dictionary<string, ImmutableArray<string>>
    {
        [DashboardTableId] = ["name", "designation", "palette", "integration", "equipment", "last_session"],
        [FramesTableId] = ["time", "file_name", "filter_used", "exposure_time", "median_hfr", "eccentricity", "fwhm", "detected_stars"],
        [LedgerTableId] = [],
        [MosaicsTableId] = MosaicColumnKeys,
    };

    // A map from table id to the ordered list of visible column keys (design-spec 5.8.2). Fresh
    // arrays, never the DefaultColumns instances: this is a mutable dictionary of mutable arrays
    // and a caller editing one must not rewrite the defaults for every later instance.
    [JsonPropertyName("columns")]
    public Dictionary<string, string[]> Columns { get; init; } =
        DefaultColumns.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());

    // Review items 5 and 6: DefaultColumns is immutable, and both branches below hand back a copy.
    // Callers get a string[] they may sort, trim or hand to a serializer without either the
    // defaults or this instance's own stored list changing underneath them.

    /// <summary>The visible column keys for a table: the stored list when present, otherwise that
    /// table's default list (design-spec 5.8.2: "A table id absent from the map uses that table's
    /// default list"), otherwise empty for a table id with no documented default.</summary>
    /// <remarks>A stored <c>null</c> counts as absent and takes the default list. Spec 5.8.2's rule
    /// that a hand-edited document still opens has to be answered here as well as at the read:
    /// <c>"columns": {"dashboard": null}</c> is the one shape that deserializes without raising
    /// anything for <c>SettingsStore.ReadDisplay</c> to drop, and the spread below would fail on the
    /// null it leaves in the map.</remarks>
    public string[] ColumnsFor(string tableId)
        => Columns.TryGetValue(tableId, out var stored) && stored is not null ? [.. stored]
            : DefaultColumns.TryGetValue(tableId, out var fallback) ? [.. fallback]
            : [];

    /// <summary>Per table id, the column widths the user dragged, keyed by column key (Phase 24
    /// R5). A column absent from its table's map auto-fits to its content, so a fresh profile
    /// stores nothing here and gains an entry only on a drag.</summary>
    [JsonPropertyName("column_widths")]
    public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; init; } = new();

    /// <summary>The stored widths for a table, as a copy; empty for a table with none or a
    /// stored <c>null</c>, for the reason <see cref="ColumnsFor"/> answers a null.</summary>
    public Dictionary<string, double> ColumnWidthsFor(string tableId)
        => ColumnWidths.TryGetValue(tableId, out var stored) && stored is not null
            ? new Dictionary<string, double>(stored, StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>This document with one column's stored width set, or removed for null, which
    /// returns that column to auto-fit. Every other table and column is left as stored.</summary>
    public DisplaySettings WithColumnWidth(string tableId, string columnKey, double? width)
    {
        var table = ColumnWidthsFor(tableId);
        if (width is { } value)
        {
            table[columnKey] = value;
        }
        else
        {
            table.Remove(columnKey);
        }

        var widths = new Dictionary<string, Dictionary<string, double>>(ColumnWidths, StringComparer.Ordinal);
        if (table.Count == 0)
        {
            widths.Remove(tableId);
        }
        else
        {
            widths[tableId] = table;
        }

        return this with { ColumnWidths = widths };
    }

    /// <summary>Spec 5.8.2's <c>sort</c> object (Phase 18): a map from table id to its stored sort.
    /// <c>mosaics</c> is its only member. Read through <see cref="MosaicsSort"/>, which applies the
    /// read rule.</summary>
    [JsonPropertyName("sort")]
    public Dictionary<string, TableSort> Sort { get; init; } = new() { [MosaicsTableId] = new() };

    /// <summary>The mosaics table's sort (spec 5.8.2): the stored entry when its key is one of
    /// <see cref="MosaicColumnKeys"/>, otherwise the default, name ascending. A custom slug reads
    /// as the default, because a custom column carries no ordering this table sorts by.</summary>
    [JsonIgnore]
    public TableSort MosaicsSort
        => Sort.TryGetValue(MosaicsTableId, out var stored) && stored?.Key is { } key && MosaicColumnKeys.Contains(key)
            ? stored
            : new TableSort();

    /// <summary>This document with one table's sort replaced, every other member as stored.</summary>
    public DisplaySettings WithSort(string tableId, TableSort sort)
        => this with { Sort = new Dictionary<string, TableSort>(Sort, StringComparer.Ordinal) { [tableId] = sort } };

    /// <summary>Spec 12.2's Dashboard filter panel (Phase 14C). Never null: a document with no
    /// <c>dashboard</c> key takes <see cref="DashboardDisplaySettings"/>'s own defaults. Declared
    /// between <see cref="Columns"/> and <see cref="TargetPage"/> so the written document matches
    /// spec 5.8.2's sample order.</summary>
    [JsonPropertyName("dashboard")]
    public DashboardDisplaySettings Dashboard { get; init; } = new();

    /// <summary>Spec 12.4's Target detail page disclosures. Never null: a document with
    /// no <c>target_page</c> key takes <see cref="TargetPageSettings"/>'s own defaults.</summary>
    [JsonPropertyName("target_page")]
    public TargetPageSettings TargetPage { get; init; } = new();

    /// <summary>Spec 12.14's Analysis page choices (Phase 17). Never null: a document
    /// with no <c>analysis</c> key takes <see cref="AnalysisDisplaySettings"/>'s own defaults.
    /// Declared after <see cref="TargetPage"/> so the written document matches spec 5.8.2's sample
    /// order.</summary>
    [JsonPropertyName("analysis")]
    public AnalysisDisplaySettings Analysis { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>One entry of spec 5.8.2's <c>sort</c> map: the column key and the direction. The
/// defaults are the mosaics table's, name ascending.</summary>
public sealed record TableSort
{
    [JsonPropertyName("key")] public string Key { get; init; } = "name";

    [JsonPropertyName("ascending")] public bool Ascending { get; init; } = true;
}
