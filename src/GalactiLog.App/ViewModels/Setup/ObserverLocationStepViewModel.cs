using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// Design-spec 12.1's step 3: latitude and longitude with range validation, an IANA timezone combo
/// defaulting to the system zone, and the imaging-night grouping checkbox. Persists
/// <c>observer_latitude</c>, <c>observer_longitude</c>, <c>observer_timezone</c> and
/// <c>use_imaging_night</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is <c>LocationTabViewModel</c>'s, called rather than copied
/// (<c>ValidateLatitude</c>, <c>ValidateLongitude</c>, <c>DefaultSystemTimezones</c>, and the
/// messages that go with them; <c>ValidateTimezone</c> is not one of them, because the combo
/// offers the machine's own zones plus the stored id and has no free-text entry to refuse): a
/// latitude the wizard
/// accepts and the Settings tab refuses is exactly the second implementation the collision map's
/// designated-owner table forbids (design-lessons rule 1). The web wizard has no range check at
/// all, and <c>SettingsStore.ValidateGeneral</c> would have thrown at the save instead of at the
/// field.
/// </para>
/// <para>
/// Both coordinates may be left blank, and blank stores null. The one-time blank-longitude nudge
/// is the web's, kept deliberately (questions.md Q34): the first Next with an empty longitude
/// reports and does not advance, the second accepts. Spec 8.3's whole longitude resolution order
/// is why the field is worth one interruption.
/// </para>
/// </remarks>
public sealed partial class ObserverLocationStepViewModel : SetupStepViewModel
{
    /// <summary>The web wizard's one-time nudge, verbatim.</summary>
    public const string BlankLongitudeNudge =
        "Imaging-night grouping falls back to UTC until longitude is set";

    private readonly Func<IReadOnlyList<string>> _systemTimezones;
    private readonly Func<string> _localTimezoneId;
    private bool _nudged;

    /// <param name="systemTimezones">The zone ids the combo offers. Defaults to
    /// <c>LocationTabViewModel.DefaultSystemTimezones</c>. A delegate so a unit test can pin the
    /// list instead of depending on the machine's zone database.</param>
    /// <param name="localTimezoneId">The system zone the combo defaults to. Defaults to
    /// <c>TimeZoneInfo.Local.Id</c>, which is what <c>GeneralSettings.DisplayTimezoneId</c> falls
    /// back to while no zone is stored.</param>
    public ObserverLocationStepViewModel(
        Func<IReadOnlyList<string>>? systemTimezones = null,
        Func<string>? localTimezoneId = null,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(post, logger)
    {
        _systemTimezones = systemTimezones ?? LocationTabViewModel.DefaultSystemTimezones;
        _localTimezoneId = localTimezoneId ?? (() => TimeZoneInfo.Local.Id);
        LatitudeText = "";
        LongitudeText = "";
        UseImagingNight = true;
    }

    /// <inheritdoc />
    public override string Title => "Observer location";

    /// <summary>Design-spec 12.1's required explanatory text, which is the Location tab's own
    /// sentence so the two surfaces say the same thing.</summary>
    public string BlankLongitudeNote { get; } = LocationTabViewModel.BlankLongitudeNote;

    /// <summary>The wizard's own intro, from <c>SetupWizard.tsx</c>'s step 2.</summary>
    public string Intro { get; } =
        "Coordinates group frames into imaging nights and drive darkness calculations.";

    /// <summary><c>general.observer_latitude</c>, as typed. Blank stores null.</summary>
    [ObservableProperty]
    public partial string LatitudeText { get; set; }

    /// <summary><c>general.observer_longitude</c>, as typed. Blank stores null.</summary>
    [ObservableProperty]
    public partial string LongitudeText { get; set; }

    /// <summary>The inline latitude refusal, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatitudeError))]
    public partial string? LatitudeError { get; private set; }

    public bool HasLatitudeError => LatitudeError is not null;

    /// <summary>The inline longitude refusal, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLongitudeError))]
    public partial string? LongitudeError { get; private set; }

    public bool HasLongitudeError => LongitudeError is not null;

    /// <summary>The observer timezone choices: "not configured" first, then every system zone.
    /// </summary>
    public ObservableCollection<TimezoneOption> Timezones { get; } = [];

    /// <summary>The chosen observer timezone.</summary>
    [ObservableProperty]
    public partial TimezoneOption? SelectedTimezone { get; set; }

    /// <summary><c>general.use_imaging_night</c>, checked by default (spec 12.1).</summary>
    [ObservableProperty]
    public partial bool UseImagingNight { get; set; }

    /// <inheritdoc />
    public override bool CanAdvance => LatitudeError is null && LongitudeError is null;

    /// <summary>Whether the one-time nudge has already been shown.</summary>
    internal bool Nudged => _nudged;

    /// <inheritdoc />
    public override void Load(GeneralSettings general)
    {
        LatitudeText = Format(general.ObserverLatitude);
        LongitudeText = Format(general.ObserverLongitude);
        UseImagingNight = general.UseImagingNight;
        LatitudeError = null;
        LongitudeError = null;

        RebuildTimezones(general.ObserverTimezone);
    }

    /// <inheritdoc />
    public override GeneralSettings Apply(GeneralSettings general)
    {
        // Parsed on the UI thread, before the mutation reaches the store's write gate. The
        // validators are the Location tab's, so the wizard and the tab agree about what a blank
        // field means.
        _ = LocationTabViewModel.ValidateLatitude(LatitudeText, out var latitude);
        _ = LocationTabViewModel.ValidateLongitude(LongitudeText, out var longitude);
        var timezone = SelectedTimezone?.Id ?? "";
        var imagingNight = UseImagingNight;

        return general with
        {
            ObserverLatitude = latitude,
            ObserverLongitude = longitude,
            ObserverTimezone = timezone,
            UseImagingNight = imagingNight,
        };
    }

    /// <inheritdoc />
    /// <remarks>Questions.md Q34: the first Next with a blank longitude reports and does not
    /// advance; a second Next accepts it and every later visit to this step advances at once.
    /// </remarks>
    public override string? BeforeAdvance()
    {
        if (_nudged || LongitudeText.Trim().Length > 0)
        {
            return null;
        }

        _nudged = true;
        return BlankLongitudeNudge;
    }

    partial void OnLatitudeTextChanged(string value)
    {
        LatitudeError = LocationTabViewModel.ValidateLatitude(value, out _);
        RaiseCanAdvanceChanged();
    }

    partial void OnLongitudeTextChanged(string value)
    {
        LongitudeError = LocationTabViewModel.ValidateLongitude(value, out _);
        RaiseCanAdvanceChanged();
    }

    // Rebuilt from the delegate on every load, so a stored-but-unlisted zone is offered exactly
    // once rather than accumulating. The same shape the Location tab uses.
    private void RebuildTimezones(string stored)
    {
        Timezones.Clear();
        Timezones.Add(new TimezoneOption("", LocationTabViewModel.NotConfiguredLabel));

        IReadOnlyList<string> zones;
        try
        {
            zones = _systemTimezones();
        }
        catch (Exception ex)
        {
            // A machine whose zone database cannot be read still gets a working wizard, with the
            // one "not configured" entry.
            Logger.LogWarning(ex, "The system timezone list could not be read");
            zones = [];
        }

        foreach (var option in TimezoneOptions.Build(zones, TimeZoneInfo.FindSystemTimeZoneById, Logger))
        {
            Timezones.Add(option);
        }

        if (stored.Length > 0 && Timezones.All(option => option.Id != stored))
        {
            Timezones.Add(new TimezoneOption(stored, $"{stored} (not on this machine)"));
        }

        // Spec 12.1: the combo defaults to the system zone on a first run, and to whatever is
        // stored on a re-run.
        var preferred = stored.Length > 0 ? stored : SafeLocalZone();
        SelectedTimezone =
            Timezones.FirstOrDefault(option => option.Id == preferred)
            ?? Timezones[0];
    }

    private string SafeLocalZone()
    {
        try
        {
            return _localTimezoneId() ?? "";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The local timezone id could not be read");
            return "";
        }
    }

    private static string Format(double? value)
        => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "";
}
