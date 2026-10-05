using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One metric group's definition: its <c>display.groups</c> key, its heading, and the ordered
/// field keys and labels inside it. The vocabulary is the web's <c>DisplayTab.tsx</c> verbatim and
/// the keys are design-spec 5.8.2's, which is the same vocabulary
/// <c>GalactiLog.Core.Settings.FrameColumns</c> joins the frame table to.
/// </summary>
public sealed record MetricGroupDefinition(string Key, string Label, IReadOnlyList<MetricFieldDefinition> Fields);

/// <summary>One field inside a metric group: its key inside <c>fields</c> and its checkbox label.
/// </summary>
public sealed record MetricFieldDefinition(string Key, string Label);

/// <summary>
/// One field checkbox on the Display tab.
/// </summary>
/// <remarks>
/// <see cref="IsGroupEnabled"/> is not a second gate on the persisted value: the field flag is
/// stored whatever the group's state is, exactly as design-spec 5.8.2 describes, and this only
/// dims and disables the checkbox while the group is off, which is what the web does.
/// </remarks>
public sealed partial class DisplayMetricFieldViewModel : ObservableObject
{
    public DisplayMetricFieldViewModel(string key, string label, bool isChecked, bool isGroupEnabled)
    {
        Key = key;
        Label = label;
        IsChecked = isChecked;
        IsGroupEnabled = isGroupEnabled;
    }

    /// <summary>The key inside its group's <c>fields</c> map.</summary>
    public string Key { get; }

    /// <summary>The checkbox label, from the web verbatim.</summary>
    public string Label { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>False dims and disables the checkbox. The stored flag is unaffected.</summary>
    [ObservableProperty]
    public partial bool IsGroupEnabled { get; set; }
}

/// <summary>
/// One collapsible metric group on design-spec 12.7's Display tab: the group's enable switch and
/// its field checkboxes, which are disabled and dimmed while the group is off.
/// </summary>
/// <remarks>
/// <para>
/// Design-spec 5.8.2's note, stated here so a reviewer does not read it as a defect:
/// <c>groups.quality.fields.hfr_stdev</c> gates nothing. Design-spec 12.4's frame table has no HFR
/// sigma column and neither does the web application. The flag stays in the defaults because the
/// <c>groups</c> defaults are the web schema verbatim, so this tab renders a checkbox for it that
/// changes nothing visible. It is deliberate, and
/// <c>MetricGroups_HfrStdev_RendersACheckboxThatGatesNothing</c> asserts it.
/// </para>
/// <para>
/// This block is the one part of the Display tab behind a Save button, matching the web's own
/// split. Everything else on that tab saves immediately.
/// </para>
/// </remarks>
public sealed partial class DisplayMetricGroupViewModel : ObservableObject
{
    /// <summary>
    /// The six groups, their headings and their 27 fields, in the web's render order. The one
    /// definition of this vocabulary in the application: the checkboxes, the saved document and
    /// <c>FrameColumns</c>'s join all read the same keys.
    /// </summary>
    public static readonly IReadOnlyList<MetricGroupDefinition> Definitions =
    [
        new("quality", "Quality Metrics",
        [
            new("hfr", "HFR"),
            new("hfr_stdev", "HFR StDev"),
            new("fwhm", "FWHM"),
            new("eccentricity", "Eccentricity"),
            new("detected_stars", "Detected Stars"),
        ]),
        new("guiding", "Guiding",
        [
            new("rms_total", "RMS Total"),
            new("rms_ra", "RMS RA"),
            new("rms_dec", "RMS Dec"),
        ]),
        new("adu", "ADU Statistics",
        [
            new("mean", "Mean"),
            new("median", "Median"),
            new("stdev", "StDev"),
            new("min", "Min"),
            new("max", "Max"),
        ]),
        new("focuser", "Focuser",
        [
            new("position", "Position"),
            new("temp", "Temperature"),
        ]),
        new("weather", "Weather",
        [
            new("ambient_temp", "Temperature"),
            new("dew_point", "Dew Point"),
            new("humidity", "Humidity"),
            new("pressure", "Pressure"),
            new("wind_speed", "Wind Speed"),
            new("wind_direction", "Wind Direction"),
            new("wind_gust", "Wind Gust"),
            new("cloud_cover", "Cloud Cover"),
            new("sky_quality", "Sky Quality"),
        ]),
        new("mount", "Mount",
        [
            new("airmass", "Airmass"),
            new("pier_side", "Pier Side"),
            new("rotator_position", "Rotator Position"),
        ]),
    ];

    private readonly Action? _onEdited;

    /// <param name="definition">One entry of <see cref="Definitions"/>.</param>
    /// <param name="stored">The group as the display document holds it, or null for a group a
    /// hand-edited document omits, which reads as its documented default (enabled for quality and
    /// guiding, every field true).</param>
    /// <param name="onEdited">Raised on any user change, so the tab can mark itself dirty.</param>
    public DisplayMetricGroupViewModel(MetricGroupDefinition definition, MetricGroupSettings? stored, Action? onEdited = null)
    {
        Key = definition.Key;
        Label = definition.Label;
        _isEnabled = stored?.Enabled ?? DefaultEnabled(definition.Key);

        Fields = [.. definition.Fields.Select(field => new DisplayMetricFieldViewModel(
            field.Key,
            field.Label,

            // A field key missing from a hand-edited document reads as true, the same rule
            // FrameColumns.IsGroupEnabled applies (design-spec 5.8.2).
            stored?.Fields is null || !stored.Fields.TryGetValue(field.Key, out var value) || value,
            _isEnabled))];

        foreach (var field in Fields)
        {
            field.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DisplayMetricFieldViewModel.IsChecked))
                {
                    _onEdited?.Invoke();
                }
            };
        }

        // The web's own initial state: DisplayTab.tsx's `collapsed` record starts empty, so every
        // group renders open.
        IsExpanded = true;

        // Assigned after the field subscriptions so the seeding above raises nothing.
        _onEdited = onEdited;
    }

    /// <summary>The <c>display.groups</c> key.</summary>
    public string Key { get; }

    /// <summary>The group heading, from the web verbatim.</summary>
    public string Label { get; }

    /// <summary>The group's field checkboxes, in the web's order.</summary>
    public IReadOnlyList<DisplayMetricFieldViewModel> Fields { get; }

    private bool _isEnabled;

    /// <summary>The group enable switch. Turning it off disables and dims every field checkbox
    /// without changing a single stored field flag.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            // Named `entry` and not `field`: inside a property accessor `field` is a C# 14
            // keyword bound to the synthesized backing field.
            foreach (var entry in Fields)
            {
                entry.IsGroupEnabled = value;
            }

            _onEdited?.Invoke();
        }
    }

    /// <summary>
    /// The chevron state, bound to the group's <c>Expander</c>. Presentation only; nothing about
    /// it is persisted, and nothing reads it back.
    /// </summary>
    /// <remarks>Starts expanded, which is what the web does: <c>DisplayTab.tsx</c> seeds its
    /// <c>collapsed</c> record empty, so every group opens with its fields showing.</remarks>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>What the Save button writes for this group.</summary>
    public MetricGroupSettings ToSettings()
        => new(IsEnabled, Fields.ToDictionary(field => field.Key, field => field.IsChecked, StringComparer.Ordinal));

    /// <summary>Rebuilds the six groups from a display document, in the documented order.
    /// </summary>
    public static ObservableCollection<DisplayMetricGroupViewModel> Build(DisplaySettings display, Action? onEdited = null)
        => [.. Definitions.Select(definition => new DisplayMetricGroupViewModel(
            definition,
            display.Groups.TryGetValue(definition.Key, out var stored) ? stored : null,
            onEdited))];

    /// <summary>The six groups as a <c>display.groups</c> map.</summary>
    public static Dictionary<string, MetricGroupSettings> ToDocument(IEnumerable<DisplayMetricGroupViewModel> groups)
        => groups.ToDictionary(group => group.Key, group => group.ToSettings(), StringComparer.Ordinal);

    /// <summary>
    /// Whether two <c>display.groups</c> maps carry the same enabled flags and the same field
    /// flags, by value.
    /// </summary>
    /// <remarks>
    /// <see cref="MetricGroupSettings"/> is a record whose <c>Fields</c> is a
    /// <see cref="Dictionary{TKey, TValue}"/>, so its generated equality compares that dictionary
    /// by reference and two structurally identical documents are never equal. This is the
    /// comparison the Display tab needs to tell a change that touched the groups from one that did
    /// not, which is what keeps a column click from tearing its editor down.
    /// </remarks>
    public static bool GroupsEqual(
        IReadOnlyDictionary<string, MetricGroupSettings> left,
        IReadOnlyDictionary<string, MetricGroupSettings> right)
    {
        foreach (var definition in Definitions)
        {
            var hasLeft = left.TryGetValue(definition.Key, out var leftGroup);
            var hasRight = right.TryGetValue(definition.Key, out var rightGroup);

            // A group key absent from either side reads as its documented default, which is what
            // FrameColumns.IsGroupEnabled does with a truncated document.
            var leftEnabled = hasLeft ? leftGroup!.Enabled : DefaultEnabled(definition.Key);
            var rightEnabled = hasRight ? rightGroup!.Enabled : DefaultEnabled(definition.Key);
            if (leftEnabled != rightEnabled)
            {
                return false;
            }

            foreach (var field in definition.Fields)
            {
                if (FieldValue(leftGroup, field.Key) != FieldValue(rightGroup, field.Key))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A missing field reads as true, the same rule the constructor applies (design-spec 5.8.2).
    private static bool FieldValue(MetricGroupSettings? group, string field)
        => group?.Fields is null || !group.Fields.TryGetValue(field, out var value) || value;

    // Design-spec 5.8.2: quality and guiding ship enabled, the other four disabled.
    private static bool DefaultEnabled(string key) => key is "quality" or "guiding";
}
