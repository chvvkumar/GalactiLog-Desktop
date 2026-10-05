using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One entry of a timezone selector: the stored IANA id and the label shown for it. A top-level
/// record so a view's <c>ComboBox.ItemTemplate</c> can name it in <c>x:DataType</c>.
/// </summary>
public sealed record TimezoneOption(string Id, string Label);

/// <summary>
/// The shared spine every timezone selector's zone entries are built from (design-lessons rule
/// 1, user ruling U4): given zone ids and a resolver from id to <see cref="TimeZoneInfo"/>,
/// returns one <see cref="TimezoneOption"/> per id labelled with its standard offset first and a
/// display name second, for example "GMT-08:00 Pacific Time (US &amp; Canada)" or
/// "GMT+00:00 Dublin, Edinburgh, Lisbon, London" (the Windows zone database
/// <see cref="TimeZoneInfo.GetSystemTimeZones"/> returns has no IANA ids, so the name is
/// <see cref="TimeZoneInfo.DisplayName"/> with its leading parenthesised "(UTC...)" prefix
/// stripped, falling back to the bare id when the display name is empty or carries nothing after
/// that prefix; provisional pending the user, kept in the one function below so a later ruling
/// changes one place). The offset is <see cref="TimeZoneInfo.BaseUtcOffset"/>, the zone's
/// standard offset, never today's actual offset, so the list does not reorder when daylight
/// saving starts or ends.
/// </summary>
/// <remarks>
/// Ordered by offset ascending, then the displayed name (<see cref="StringComparer.OrdinalIgnoreCase"/>),
/// so negative offsets sort before GMT and GMT before positive offsets. An id the resolver cannot
/// resolve, for example one this machine's zone database does not carry, is kept rather than
/// dropped, labelled with the bare id, and sorted after every resolved zone, ordered among
/// itself by id. A resolver failure of any kind (not only an unknown id: a permission failure
/// reading the zone registry, say) is treated the same way and never reaches the caller, because
/// this list is built eagerly, inside this method, rather than through a deferred iterator whose
/// exceptions would otherwise surface on whatever thread first enumerates it. A caller that needs
/// a special entry (an empty "not configured" id, a convenience id that mirrors another field, a
/// stored-but-unlisted id) adds it around this list: the builder only knows about ids that
/// resolve to a real offset or fail to resolve at all.
/// </remarks>
internal static class TimezoneOptions
{
    public static List<TimezoneOption> Build(
        IEnumerable<string> zoneIds,
        Func<string, TimeZoneInfo> resolve,
        ILogger? logger = null)
    {
        var resolved = new List<(string Id, TimeSpan Offset, string Name)>();
        var unresolved = new List<string>();

        foreach (var id in zoneIds)
        {
            TimeZoneInfo? zone;
            try
            {
                zone = resolve(id);
            }
            catch (Exception ex)
            {
                // Any resolver failure, not only an unknown id: FindSystemTimeZoneById also
                // documents SecurityException and OutOfMemoryException. One unreadable zone costs
                // one bare-id label, not a throw on the UI thread.
                logger?.LogDebug(ex, "Timezone {ZoneId} could not be resolved; offering its bare id", id);
                zone = null;
            }

            if (zone is null)
            {
                unresolved.Add(id);
            }
            else
            {
                resolved.Add((id, zone.BaseUtcOffset, DisplayedName(zone, id)));
            }
        }

        var options = new List<TimezoneOption>(resolved.Count + unresolved.Count);
        options.AddRange(resolved
            .OrderBy(entry => entry.Offset)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new TimezoneOption(entry.Id, FormatLabel(entry.Offset, entry.Name))));
        options.AddRange(unresolved
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new TimezoneOption(Id: id, Label: id)));

        return options;
    }

    /// <summary>
    /// The zone's standard-offset prefix alone ("GMT+05:30"), with no display name appended: the
    /// half of <see cref="FormatLabel"/> the frame table's Time column header shares (Phase 14C
    /// fixer-list item 20), so the header and the two selectors can never format an offset two
    /// different ways. Exposed rather than duplicated (design-lessons rule 1): the frame table
    /// builds its own "Time (...)" wrapper around this, the selectors build a name after it, and
    /// neither writes its own copy of the sign-and-magnitude arithmetic.
    /// </summary>
    internal static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return $"GMT{sign}{magnitude.Hours:D2}:{magnitude.Minutes:D2}";
    }

    // Windows display names read "(UTC-08:00) Pacific Time (US & Canada)"; the offset half is
    // already in the label, so only the name after the prefix is wanted. A display name with no
    // parenthesised prefix (an ICU-mapped IANA name, say) is used as-is.
    private static string DisplayedName(TimeZoneInfo zone, string id)
    {
        var raw = zone.DisplayName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return id;
        }

        var name = raw;
        if (name.Length > 0 && name[0] == '(')
        {
            var closeParen = name.IndexOf(')');
            if (closeParen >= 0)
            {
                name = name[(closeParen + 1)..];
            }
        }

        name = name.Trim();
        return name.Length == 0 ? id : name;
    }

    private static string FormatLabel(TimeSpan offset, string name)
        => $"{FormatOffset(offset)} {name}";
}

/// <summary>
/// Design-spec 12.7's Location tab: observer latitude, longitude, name and IANA timezone, the
/// imaging-night grouping checkbox, the display timezone, and the 24-hour clock checkbox.
/// </summary>
/// <remarks>
/// <para>
/// Every field saves immediately on commit, with a roll-back on failure, which is what the web's
/// observer-location block does (it has no Save button). Range errors are inline and a field in
/// error does not save, also matching the web.
/// </para>
/// <para>
/// The inline checks are a usability layer in front of <c>SettingsStore.ValidateGeneral</c>, which
/// range-checks both coordinates itself and stays the enforcement point (design-lessons rule 2).
/// The static validators below are the single definition of those rules for this application:
/// Task 9's setup wizard calls them rather than growing a second latitude rule that this tab would
/// then disagree with (collision map, designated owners).
/// </para>
/// <para>
/// Two timezone keys live here and they are not the same thing. <c>observer_timezone</c> feeds
/// session derivation (design-spec 8.3) and the empty string means "not configured".
/// <c>timezone</c> is display formatting only and never affects <c>session_date</c> (design-spec
/// 5.8.1). They are labelled so a user can tell them apart.
/// </para>
/// <para>
/// Questions.md Q14: observer coordinates come from settings and never from FITS headers.
/// </para>
/// </remarks>
public sealed partial class LocationTabViewModel : GeneralSettingsTabViewModel
{
    /// <summary>The web's inline latitude message, verbatim.</summary>
    public const string LatitudeRangeMessage = "Must be between -90 and 90";

    /// <summary>The web's inline longitude message, verbatim.</summary>
    public const string LongitudeRangeMessage = "Must be between -180 and 180";

    /// <summary>Shown for a value that is not a number at all. The web's input is
    /// <c>type=number</c>, which the platform enforces; a plain Avalonia TextBox does not, so the
    /// port needs a message the web never has to show.</summary>
    public const string NotANumberMessage = "Enter a number, or leave it blank.";

    /// <summary>Design-spec 8.3 and 12.1 step 3, stated on the tab because this is where a user
    /// clears the field.</summary>
    public const string BlankLongitudeNote =
        "Without a longitude, sessions group on UTC midnight instead of on local solar midnight.";

    /// <summary>The first entry of the observer timezone selector. Its value is the empty string,
    /// which is what "not configured" is stored as (design-spec 5.8.1).</summary>
    public const string NotConfiguredLabel = "Select a timezone";

    /// <summary>The display picker's first entry, whose value is the empty string: the display
    /// timezone follows the observer timezone until the user picks one (polish wave 3, ruling 1).
    /// </summary>
    public const string FollowObserverLabel = "Same as observer timezone";

    /// <summary>The same entry while no observer timezone is stored, when following it means
    /// this machine's zone.</summary>
    public const string FollowObserverUnsetLabel = "Same as observer timezone (not set, using this PC's)";

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>. Not a
    /// <c>GetGeneral</c> plus <c>SaveGeneral</c> pair: see the base class.</param>
    /// <param name="systemTimezones">The zone ids offered by the two selectors. Defaults to
    /// <c>TimeZoneInfo.GetSystemTimeZones()</c>. A delegate so a unit test can pin the list
    /// instead of depending on the machine's zone database.</param>
    public LocationTabViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<IReadOnlyList<string>>? systemTimezones = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null)
        : base(load, mutateGeneral, post, logger, subscribeGeneralChanged, unsubscribeGeneralChanged)
    {
        _systemTimezones = systemTimezones ?? DefaultSystemTimezones;

        // Assigned before anything can observe them; every change handler returns early while the
        // base class's applying guard is set, and it is set for the whole of the first publish.
        ObserverName = "";
        LatitudeText = "";
        LongitudeText = "";
        SelectedObserverTimezone = null;
        SelectedDisplayTimezone = null;

        Load();
    }

    private readonly Func<IReadOnlyList<string>> _systemTimezones;

    /// <summary><c>general.observer_name</c>. Empty text is stored as null, matching the web's
    /// <c>value || null</c>.</summary>
    [ObservableProperty]
    public partial string ObserverName { get; set; }

    /// <summary><c>general.observer_latitude</c> as typed. Blank clears the stored value to
    /// null.</summary>
    [ObservableProperty]
    public partial string LatitudeText { get; set; }

    /// <summary><c>general.observer_longitude</c> as typed. Blank clears the stored value to
    /// null.</summary>
    [ObservableProperty]
    public partial string LongitudeText { get; set; }

    /// <summary>The inline latitude message, or null. A field in error does not save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatitudeError))]
    public partial string? LatitudeError { get; private set; }

    /// <summary>The inline longitude message, or null. A field in error does not save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLongitudeError))]
    public partial string? LongitudeError { get; private set; }

    /// <summary>The inline observer timezone message, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimezoneError))]
    public partial string? TimezoneError { get; private set; }

    public bool HasLatitudeError => LatitudeError is not null;

    public bool HasLongitudeError => LongitudeError is not null;

    public bool HasTimezoneError => TimezoneError is not null;

    /// <summary>True while <c>observer_longitude</c> is unset, which is when design-spec 8.3's
    /// UTC-midnight fallback applies. The tab shows <see cref="BlankLongitudeNote"/> then.
    /// </summary>
    [ObservableProperty]
    public partial bool LongitudeIsBlank { get; private set; }

    /// <summary>The observer timezone selector (design-spec 8.3). Its first entry is the empty
    /// id, which is the stored form of "not configured".</summary>
    public ObservableCollection<TimezoneOption> ObserverTimezones { get; } = [];

    /// <summary>The display timezone selector (design-spec 5.8.1's separate <c>timezone</c> key).
    /// Display formatting only.</summary>
    public ObservableCollection<TimezoneOption> DisplayTimezones { get; } = [];

    [ObservableProperty]
    public partial TimezoneOption? SelectedObserverTimezone { get; set; }

    [ObservableProperty]
    public partial TimezoneOption? SelectedDisplayTimezone { get; set; }

    /// <summary><c>general.use_imaging_night</c> (design-spec 8.2). Saves immediately.</summary>
    [ObservableProperty]
    public partial bool UseImagingNight { get; set; }

    /// <summary><c>general.use_24h_time</c>. Saves immediately.</summary>
    [ObservableProperty]
    public partial bool Use24HTime { get; set; }

    /// <summary>
    /// Design-spec 8.2's session date is written at ingest, so changing
    /// <see cref="UseImagingNight"/> recomputes nothing. The web's toast claims sessions are being
    /// recomputed; the port says what actually happens instead.
    /// </summary>
    public const string ImagingNightNote =
        "Session grouping is recorded when a frame is ingested, so this takes effect on the next scan. "
        + "Existing frames keep the grouping they were scanned with.";

    /// <summary>
    /// Design-spec 5.8.1's latitude rule, as one function. Returns null when the text is
    /// acceptable and sets <paramref name="value"/> to the parsed coordinate, or to null for a
    /// blank field, which clears the stored value.
    /// </summary>
    /// <remarks>Task 9's setup wizard calls this rather than writing a second latitude rule.
    /// The message is the web's, verbatim.</remarks>
    public static string? ValidateLatitude(string? text, out double? value)
        => ValidateCoordinate(text, -90d, 90d, LatitudeRangeMessage, out value);

    /// <summary>Design-spec 5.8.1's longitude rule, on the same terms as
    /// <see cref="ValidateLatitude"/>.</summary>
    public static string? ValidateLongitude(string? text, out double? value)
        => ValidateCoordinate(text, -180d, 180d, LongitudeRangeMessage, out value);

    /// <summary>
    /// Design-spec 5.8.1's <c>observer_timezone</c> rule: the empty string passes and means "not
    /// configured"; anything else must be a zone the platform knows. Returns null when the id is
    /// acceptable, otherwise the web backend's message verbatim.
    /// </summary>
    /// <remarks>Also used for <c>general.timezone</c>, which has the same shape and no empty
    /// case in practice. Task 9's wizard calls this one too.</remarks>
    public static string? ValidateTimezone(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return null;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return $"'{id}' is not a known IANA time zone";
        }
    }

    /// <summary>The machine's zone ids, sorted, as both selectors offer them.</summary>
    public static IReadOnlyList<string> DefaultSystemTimezones()
        => [.. TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    protected override void ApplyDocument(GeneralSettings general)
    {
        ObserverName = general.ObserverName ?? "";
        LatitudeText = Format(general.ObserverLatitude);
        LongitudeText = Format(general.ObserverLongitude);
        UseImagingNight = general.UseImagingNight;
        Use24HTime = general.Use24HTime;

        LatitudeError = null;
        LongitudeError = null;
        TimezoneError = null;
        LongitudeIsBlank = general.ObserverLongitude is null;

        RebuildTimezones(general);
    }

    // ---- the immediate saves ------------------------------------------------------------------

    partial void OnObserverNameChanged(string value)
    {
        var trimmed = value.Trim();
        var previous = Saved.ObserverName ?? "";
        ImmediateSave(
            general => general with { ObserverName = trimmed.Length == 0 ? null : trimmed },
            "Observer name saved",
            () => ObserverName = previous);
    }

    partial void OnLatitudeTextChanged(string value)
    {
        var previous = Format(Saved.ObserverLatitude);
        LatitudeError = ValidateLatitude(value, out var parsed);
        if (LatitudeError is not null)
        {
            // The web does not save on blur while an inline error is present. Neither does this.
            return;
        }

        ImmediateSave(
            general => general with { ObserverLatitude = parsed },
            "Observer latitude saved",
            () => LatitudeText = previous);
    }

    partial void OnLongitudeTextChanged(string value)
    {
        var previous = Format(Saved.ObserverLongitude);
        LongitudeError = ValidateLongitude(value, out var parsed);

        // Recomputed from the text, before the error early-return (review minor 5). Clearing a
        // valid longitude to a typo is exactly when the spec 8.3 UTC-midnight note matters most,
        // and updating it only on the success path left it showing the previous value's state.
        LongitudeIsBlank = value.Trim().Length == 0;

        if (LongitudeError is not null)
        {
            return;
        }

        ImmediateSave(
            general => general with { ObserverLongitude = parsed },
            "Observer longitude saved",
            () => LongitudeText = previous);
    }

    partial void OnSelectedObserverTimezoneChanged(TimezoneOption? oldValue, TimezoneOption? newValue)
    {
        if (newValue is null)
        {
            return;
        }

        TimezoneError = ValidateTimezone(newValue.Id);
        if (TimezoneError is not null)
        {
            return;
        }

        var id = newValue.Id;
        ImmediateSave(
            general => general with { ObserverTimezone = id },
            id.Length == 0 ? "Observer timezone cleared" : "Observer timezone saved",
            () => SelectedObserverTimezone = oldValue);
    }

    partial void OnSelectedDisplayTimezoneChanged(TimezoneOption? oldValue, TimezoneOption? newValue)
    {
        if (newValue is null || ValidateTimezone(newValue.Id) is not null)
        {
            return;
        }

        var id = newValue.Id;
        ImmediateSave(
            general => general with { Timezone = id },
            id.Length == 0 ? "Display timezone follows the observer timezone" : "Display timezone saved",
            () => SelectedDisplayTimezone = oldValue);
    }

    partial void OnUseImagingNightChanged(bool value)
        => ImmediateSave(
            general => general with { UseImagingNight = value },
            value ? "Imaging night grouping enabled" : "Imaging night grouping disabled",
            () => UseImagingNight = !value);

    partial void OnUse24HTimeChanged(bool value)
        => ImmediateSave(
            general => general with { Use24HTime = value },
            value ? "24 hour clock" : "12 hour clock",
            () => Use24HTime = !value);

    // ---- selector construction ----------------------------------------------------------------

    // Rebuilt on every publish, so the stored-but-unlisted entry below can never accumulate.
    private void RebuildTimezones(GeneralSettings general)
    {
        var zones = _systemTimezones();

        ObserverTimezones.Clear();

        // The web's deliberate first option: its value is the empty string, which is the stored
        // form of "not configured" (design-spec 5.8.1).
        ObserverTimezones.Add(new TimezoneOption("", NotConfiguredLabel));

        // The web's convenience option, whose value is the display timezone.
        if (general.Timezone.Length > 0)
        {
            ObserverTimezones.Add(new TimezoneOption(
                general.Timezone,
                $"Same as display timezone ({general.Timezone})"));
        }

        // Built once: both selectors offer the same zone ids through the same resolver, so the
        // labelled, ordered list is computed a single time and added to each collection.
        var zoneOptions = TimezoneOptions.Build(zones, TimeZoneInfo.FindSystemTimeZoneById, Logger);

        foreach (var option in zoneOptions)
        {
            ObserverTimezones.Add(option);
        }

        // A stored zone this machine does not list. Offered as its own entry rather than silently
        // dropped, which would rewrite a value the user chose on another machine.
        if (general.ObserverTimezone.Length > 0
            && !zones.Contains(general.ObserverTimezone, StringComparer.Ordinal))
        {
            ObserverTimezones.Add(new TimezoneOption(
                general.ObserverTimezone,
                $"{general.ObserverTimezone} (not on this machine)"));
        }

        SelectedObserverTimezone =
            ObserverTimezones.FirstOrDefault(option => option.Id == general.ObserverTimezone)
            ?? ObserverTimezones[0];

        DisplayTimezones.Clear();
        DisplayTimezones.Add(new TimezoneOption(
            "",
            general.ObserverTimezone.Length > 0 ? FollowObserverLabel : FollowObserverUnsetLabel));
        foreach (var option in zoneOptions)
        {
            DisplayTimezones.Add(option);
        }

        if (general.Timezone.Length > 0 && !zones.Contains(general.Timezone, StringComparer.Ordinal))
        {
            DisplayTimezones.Add(new TimezoneOption(
                general.Timezone,
                $"{general.Timezone} (not on this machine)"));
        }

        SelectedDisplayTimezone =
            DisplayTimezones.FirstOrDefault(option => option.Id == general.Timezone)
            ?? DisplayTimezones[0];
    }

    private static string? ValidateCoordinate(string? text, double min, double max, string rangeMessage, out double? value)
    {
        value = null;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            // Blank clears the stored value to null, which is the documented default.
            return null;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            return NotANumberMessage;
        }

        if (parsed < min || parsed > max)
        {
            return rangeMessage;
        }

        value = parsed;
        return null;
    }

    // Round-trip format (review minor 6). The web's step="0.0001" governs input granularity, not
    // storage, and nothing refuses a stored coordinate with more precision than that:
    // ValidateGeneral only range-checks. "R" therefore renders whatever is stored exactly, so the
    // box never shows a truncated value that the next commit would then persist as the truth.
    private static string Format(double? value)
        => value is null ? "" : value.Value.ToString("R", CultureInfo.InvariantCulture);
}
