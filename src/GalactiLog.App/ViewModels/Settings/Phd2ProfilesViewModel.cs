using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>One telescope choice in a profile row's picker: a telescope name from the grouping
/// editor above (a canonical group name, or a discovered name in no group), or the empty value
/// meaning "not mapped" (spec 12.7).</summary>
public sealed record Phd2TelescopeOption(string Value, string Label);

/// <summary>
/// One row of spec 12.7's PHD2 profiles panel: the facts the guide logs themselves recorded
/// (never edited here), the telescope, timezone and site the map maps this profile to, and where
/// the zone and the site that actually resolve for it come from.
/// </summary>
public sealed partial class Phd2ProfileRowViewModel : ObservableObject
{
    private readonly Phd2ProfilesViewModel _owner;

    // The last value this row actually wrote (or read on load), so a refused edit reverts the box
    // to what is really stored rather than to a compile-time default (the shape the Diagnostics
    // retention editors and LocationTabViewModel's coordinate boxes both use).
    private string? _storedTelescope;
    private string _storedTimezone = "";
    private double? _storedLatitude;
    private double? _storedLongitude;

    private bool _seeding;

    internal Phd2ProfileRowViewModel(
        Phd2ProfilesViewModel owner,
        Phd2ProfileRow facts,
        Phd2ProfileEntry entry,
        IReadOnlyList<Phd2TelescopeOption> telescopeOptions,
        IReadOnlyList<TimezoneOption> timezoneOptions)
    {
        _owner = owner;
        Profile = facts.Profile;
        ProfileDisplay = facts.Profile.Length == 0 ? NoEquipmentProfileLabel : facts.Profile;
        // Captioned in the markup, so an absent fact says what is missing rather than leaving a
        // labelled gap (review P2-3).
        GuideCameraText = string.IsNullOrEmpty(facts.GuideCamera) ? NotInTheLogText : facts.GuideCamera;
        FocalLengthText = facts.FocalLengthMm is { } focal
            ? focal.ToString("0.#", CultureInfo.InvariantCulture) + " mm"
            : NotInTheLogText;
        PixelScaleText = facts.PixelScaleArcsec is { } scale
            ? scale.ToString("0.###", CultureInfo.InvariantCulture)
            : NotInTheLogText;
        SessionCountText = facts.SessionCount.ToString(CultureInfo.InvariantCulture);
        FirstSeen = facts.FirstSeen;
        LastSeen = facts.LastSeen;
        FirstSeenIsLogClock = facts.FirstSeenIsLogClock;
        LastSeenIsLogClock = facts.LastSeenIsLogClock;

        TelescopeOptions = telescopeOptions;
        TimezoneOptions = timezoneOptions;

        Apply(entry);
    }

    /// <summary>The panel labels an empty-string profile the way spec 10.9's warning does.
    /// </summary>
    public const string NoEquipmentProfileLabel = "(no equipment profile)";

    /// <summary>Spec 12.7 and spec 7.6's ASIAIR case.</summary>
    public const string NotInTheLogText = "not in the log";

    /// <summary>The raw profile key, the empty string for a session with no recorded profile.
    /// </summary>
    public string Profile { get; }

    public string ProfileDisplay { get; }
    public string GuideCameraText { get; }
    public string FocalLengthText { get; }
    public string PixelScaleText { get; }
    public string SessionCountText { get; }

    /// <summary>The stored "seen" values of spec 12.7's table, kept raw, each with the basis the
    /// read gave it: a resolved UTC instant, or the guide log's own local wall clock.</summary>
    internal DateTime FirstSeen { get; }

    internal DateTime LastSeen { get; }

    /// <summary>Whether <see cref="FirstSeen"/> is the guide log's wall clock rather than a
    /// resolved instant. Read from the row the query produced and never re-derived from the zone
    /// that resolves now: setting a zone on this row changes what FUTURE reads will hold, not what
    /// this row already holds, and the re-derive that rewrites the stored value is asynchronous and
    /// does not run at all while guide log reading is off (fix-wave review P2-1).</summary>
    internal bool FirstSeenIsLogClock { get; }

    internal bool LastSeenIsLogClock { get; }

    /// <summary>First and last seen as the rest of the application renders an instant: the
    /// display timezone, through the one formatter, with the log-clock case marked rather than
    /// silently shown on a different basis. Set by
    /// <see cref="Phd2ProfilesViewModel.DescribeResolution"/>, which is where the display timezone
    /// is in hand.</summary>
    [ObservableProperty]
    public partial string FirstSeenText { get; private set; } = "";

    [ObservableProperty]
    public partial string LastSeenText { get; private set; } = "";

    public IReadOnlyList<Phd2TelescopeOption> TelescopeOptions { get; }
    public IReadOnlyList<TimezoneOption> TimezoneOptions { get; }

    [ObservableProperty]
    public partial Phd2TelescopeOption? SelectedTelescope { get; set; }

    [ObservableProperty]
    public partial TimezoneOption? SelectedTimezone { get; set; }

    [ObservableProperty]
    public partial string LatitudeText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatitudeError))]
    public partial string? LatitudeError { get; private set; }

    [ObservableProperty]
    public partial string LongitudeText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLongitudeError))]
    public partial string? LongitudeError { get; private set; }

    public bool HasLatitudeError => LatitudeError is not null;
    public bool HasLongitudeError => LongitudeError is not null;

    /// <summary>Below the editors: the zone that actually resolves for this profile and where it
    /// came from, or the unset warning of spec 10.9's <c>phd2_timezone_unset</c>.</summary>
    [ObservableProperty]
    public partial string ZoneResolutionText { get; private set; } = "";

    [ObservableProperty]
    public partial bool ZoneIsUnset { get; private set; }

    /// <summary>Below the editors: the site that actually resolves for this profile, latitude and
    /// longitude reported independently because each can inherit on its own (spec 7.6).</summary>
    [ObservableProperty]
    public partial string SiteResolutionText { get; private set; } = "";

    /// <summary>Re-seeds every editor and the resolution text from a freshly read entry, without
    /// writing anything back (the constructor's seeding, and a reload's).</summary>
    internal void Apply(Phd2ProfileEntry entry)
    {
        _seeding = true;
        try
        {
            _storedTelescope = entry.Telescope;
            _storedTimezone = entry.Timezone;
            _storedLatitude = entry.Latitude;
            _storedLongitude = entry.Longitude;

            SelectedTelescope = TelescopeOptions.FirstOrDefault(
                option => string.Equals(option.Value, entry.Telescope ?? "", StringComparison.Ordinal))
                ?? TelescopeOptions[0];
            SelectedTimezone = TimezoneOptions.FirstOrDefault(
                option => string.Equals(option.Id, entry.Timezone, StringComparison.Ordinal))
                ?? TimezoneOptions[0];
            LatitudeText = entry.Latitude is { } lat ? lat.ToString("R", CultureInfo.InvariantCulture) : "";
            LongitudeText = entry.Longitude is { } lon ? lon.ToString("R", CultureInfo.InvariantCulture) : "";
            LatitudeError = null;
            LongitudeError = null;
        }
        finally
        {
            _seeding = false;
        }

        RefreshResolution();
    }

    internal void RefreshResolution() => _owner.DescribeResolution(this);

    internal void SetResolution(
        string zoneText, bool zoneUnset, string siteText, string firstSeenText, string lastSeenText)
    {
        ZoneResolutionText = zoneText;
        ZoneIsUnset = zoneUnset;
        SiteResolutionText = siteText;
        FirstSeenText = firstSeenText;
        LastSeenText = lastSeenText;
    }

    partial void OnSelectedTelescopeChanged(Phd2TelescopeOption? value)
    {
        if (_seeding || value is null)
        {
            return;
        }

        Commit(entry => entry with { Telescope = value.Value.Length == 0 ? null : value.Value });
    }

    partial void OnSelectedTimezoneChanged(TimezoneOption? value)
    {
        if (_seeding || value is null)
        {
            return;
        }

        Commit(entry => entry with { Timezone = value.Id });
    }

    partial void OnLatitudeTextChanged(string value)
    {
        if (_seeding)
        {
            return;
        }

        LatitudeError = LocationTabViewModel.ValidateLatitude(value, out var parsed);
        if (LatitudeError is not null)
        {
            return;
        }

        Commit(entry => entry with { Latitude = parsed });
    }

    partial void OnLongitudeTextChanged(string value)
    {
        if (_seeding)
        {
            return;
        }

        LongitudeError = LocationTabViewModel.ValidateLongitude(value, out var parsed);
        if (LongitudeError is not null)
        {
            return;
        }

        Commit(entry => entry with { Longitude = parsed });
    }

    private void Commit(Func<Phd2ProfileEntry, Phd2ProfileEntry> edit)
    {
        var revert = CaptureRevert();
        _owner.SaveCommand.Execute(new Phd2RowEdit(this, edit, revert));
    }

    private Action CaptureRevert()
    {
        var telescope = _storedTelescope;
        var timezone = _storedTimezone;
        var latitude = _storedLatitude;
        var longitude = _storedLongitude;
        return () => Apply(new Phd2ProfileEntry
        {
            Telescope = telescope,
            Timezone = timezone,
            Latitude = latitude,
            Longitude = longitude,
        });
    }

    // The committed value, read by the owner just before it leaves the UI thread (TRACKING
    // section 6 item 24: a value bound to the UI is read on the UI thread, never off it).
    internal void MarkCommitted(Phd2ProfileEntry entry)
    {
        _storedTelescope = entry.Telescope;
        _storedTimezone = entry.Timezone;
        _storedLatitude = entry.Latitude;
        _storedLongitude = entry.Longitude;
    }
}

/// <summary>One row's pending edit, captured on the UI thread before the write leaves it. Public
/// because <see cref="Phd2ProfilesViewModel.SaveCommand"/>, the generated command it is the
/// parameter type of, is itself public (CS0053: a public member's type cannot be less accessible
/// than the member).</summary>
public sealed record Phd2RowEdit(
    Phd2ProfileRowViewModel Row, Func<Phd2ProfileEntry, Phd2ProfileEntry> Edit, Action Revert);

/// <summary>
/// Spec 12.7's PHD2 profiles panel on the Equipment tab: the profiles the guide logs themselves
/// named, read through <see cref="Phd2ProfilesQuery"/>, each mapped to a telescope, a timezone and
/// a site through <c>general.phd2_profile_map</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every collaborator is a delegate, never a store and never a context (the shape every other
/// Settings view-model here has), so the panel constructs in a unit test with lambdas and no
/// database. Every write goes through <see cref="SettingsStore.MutateGeneral" />, and the map's
/// only door is <c>GalactiLog.Core.Phd2.Phd2Profiles</c>: nothing here reads or writes
/// <c>general.phd2_profile_map</c> by hand.
/// </para>
/// <para>
/// The correlation re-run is not this panel's to dispatch and this panel holds no callback for it.
/// <c>SettingsStore.MutateGeneral</c> raises <c>Phd2GuidingInputsChanged</c> itself, on the same
/// <see cref="Phd2Profiles.SameMap"/> comparison, for every writer of the general document rather
/// than for this panel alone (design-lessons rule 2: the choke point, not a convention each writer
/// remembers). A callback here would queue a second re-run for every edit this panel makes.
/// What the panel does own is spec 12.7's sentence: it <em>says</em> a re-run has been queued,
/// from its own knowledge that its write changed the map, and never blocks on the re-run.
/// </para>
/// </remarks>
public sealed partial class Phd2ProfilesViewModel : ObservableObject
{
    /// <summary>Spec 12.7's empty choice for the telescope picker.</summary>
    public const string NotMappedLabel = "Not mapped";

    /// <summary>Spec 12.7's empty choice for the timezone picker, verbatim.</summary>
    public const string InheritTimezoneLabel = "Use the observer timezone";

    /// <summary>Spec 12.7's last paragraph: the panel is empty until a scan has read a guide log.
    /// </summary>
    public const string EmptyStateMessage =
        "No PHD2 guide logs have been read yet. Turn on \"Read PHD2 guide logs\" on the Library "
        + "tab and run a scan to populate this panel.";

    /// <summary>Spec 10.9's <c>phd2_timezone_unset</c> warning, ported to a single profile's row.
    /// </summary>
    public const string UnsetZoneWarning =
        "No timezone is resolved for this profile's guiding sessions. Set one here, or set the "
        + "observer timezone on the Location tab.";

    /// <summary>Spec 12.7's re-run sentence, shown after a save that moved the map.</summary>
    public const string ReRunQueuedMessage =
        "A guiding re-run has been queued. Your frames are being re-checked in the background.";

    /// <summary>Spec 12.7: what the panel says while a re-run is still owed, read at load from
    /// <c>general.phd2_correlation_pending</c>. An earlier re-check was queued and did not finish,
    /// so the work is owed rather than done.</summary>
    public const string ReRunOwedMessage =
        "A guiding re-check of your frames has not finished yet. It carries on in the background, "
        + "and starts again the next time you open GalactiLog.";

    /// <summary>The same state with guide log reading switched off. The sentence must not promise
    /// a re-check that cannot run, so it names the switch that is stopping it and where that
    /// switch is, rather than the background work.</summary>
    public const string ReRunOwedButDisabledMessage =
        "A guiding re-check of your frames is still owed, but reading PHD2 guide logs is switched "
        + "off on the Library tab. Turn it back on to let the re-check run.";

    /// <summary>How a stored telescope the Equipment page no longer lists, neither as a canonical
    /// group name nor as a discovered name of the library, is labelled in the picker, so the row's
    /// own resolution line and its picker cannot contradict each other. The Location tab labels a
    /// stored-but-unlisted zone the same way.</summary>
    public const string NotInTheEquipmentListSuffix = " (not in the equipment list)";

    /// <summary>How a stored zone this machine's zone database does not list is labelled, verbatim
    /// as <c>LocationTabViewModel.RebuildTimezones</c> labels it.</summary>
    public const string NotOnThisMachineSuffix = " (not on this machine)";

    /// <summary>What first and last seen carry when no zone resolved for the profile: the guide
    /// log wrote a bare wall clock and nothing can place it on a timeline, so the value says so
    /// rather than reading like an instant in the display timezone (Task 6b review P3-6).
    /// </summary>
    public const string UnzonedSeenSuffix = " (log clock, no timezone set)";

    private readonly Func<GeneralSettings> _loadGeneral;
    private readonly Func<IReadOnlyList<Phd2ProfileRow>> _loadRows;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutateGeneral;
    private readonly Func<IReadOnlyList<string>> _knownTelescopes;
    private readonly Func<IReadOnlyList<string>> _systemTimezones;
    private readonly Func<string, TimeZoneInfo> _resolveTimezone;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private GeneralSettings _general = new();
    private int _generation;

    /// <param name="loadGeneral">Normally <c>SettingsStore.GetGeneral</c>. Called off the UI
    /// thread, in the same background pass as <paramref name="loadRows"/>.</param>
    /// <param name="loadRows">Normally <c>Phd2ProfilesQuery.Read</c>.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>. Every write goes
    /// through this, never a direct read-modify-write of the stored document.</param>
    /// <param name="knownTelescopes">Every telescope name the picker offers, normally
    /// <c>EquipmentTabViewModel.KnownTelescopes</c>: the canonical names of the grouping editor
    /// above this panel together with the library's discovered telescope names that belong to no
    /// group. Canonical names alone left the picker empty, and every profile unmappable, on a
    /// library whose reader has never created an alias group.</param>
    /// <param name="systemTimezones">The zone ids the timezone picker offers, the same shared list
    /// <c>LocationTabViewModel</c>'s two selectors use. Defaults to the machine's zone database.
    /// </param>
    /// <param name="resolveTimezone">Defaults to <c>TimeZoneInfo.FindSystemTimeZoneById</c>.
    /// </param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load or save is logged, never rethrown on the UI
    /// thread.</param>
    public Phd2ProfilesViewModel(
        Func<GeneralSettings> loadGeneral,
        Func<IReadOnlyList<Phd2ProfileRow>> loadRows,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<IReadOnlyList<string>> knownTelescopes,
        Func<IReadOnlyList<string>>? systemTimezones = null,
        Func<string, TimeZoneInfo>? resolveTimezone = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _loadGeneral = loadGeneral;
        _loadRows = loadRows;
        _mutateGeneral = mutateGeneral;
        _knownTelescopes = knownTelescopes;
        _systemTimezones = systemTimezones ?? LocationTabViewModel.DefaultSystemTimezones;
        _resolveTimezone = resolveTimezone ?? TimeZoneInfo.FindSystemTimeZoneById;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        Load();
    }

    public ObservableCollection<Phd2ProfileRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasErrorMessage => ErrorMessage is not null;

    /// <summary>Spec 12.7's "the panel says a re-run has been queued rather than blocking on it",
    /// set from this panel's own knowledge that its write moved the map and cleared by the next
    /// save. The panel never names, holds or awaits the runner: the re-run is dispatched from
    /// <c>SettingsStore</c>'s own change event, for every writer of the general document.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasPendingMessage))]
    public partial string? StatusMessage { get; private set; }

    public bool HasStatusMessage => StatusMessage is not null;

    /// <summary>Spec 12.7's "the panel says a re-run is still owed" sentence, set at every load
    /// from <c>general.phd2_correlation_pending</c> and from whether guide log reading is on.
    /// Null when nothing is owed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMessage))]
    public partial string? PendingMessage { get; private set; }

    /// <summary>
    /// Precedence, pinned by
    /// <c>Phd2ProfilesViewModelTests.TheQueuedSentence_HidesTheOwedSentence_WhileItIsUp</c>: the
    /// queued sentence wins. It reports what THIS user just did and is the newer fact; the owed
    /// sentence is the standing state read at load, and the flag is still true at that moment
    /// precisely because the save that produced the queued sentence set it. Showing both would
    /// read as two separate re-runs.
    /// </summary>
    public bool HasPendingMessage => PendingMessage is not null && StatusMessage is null;

    /// <summary>The empty state: no row has ever been read from a guide log (spec 12.7's last
    /// paragraph). Never shown while loading or after a failed read, which have their own states.
    /// </summary>
    public bool IsEmpty => !IsLoading && !LoadFailed && Rows.Count == 0;

    /// <summary>The in-flight read, so a test awaits it instead of sleeping (TRACKING section 6
    /// item 24: the query runs off the UI thread).</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The tail of the write chain, so a test awaits a commit instead of sleeping.
    /// </summary>
    internal Task PendingWrite { get; private set; } = Task.CompletedTask;

    public void Reload() => Load();

    // The thread inside _mutateGeneral, so a change event raised by this panel's own write is
    // recognised as its own. Zero while no write is in flight. The shape
    // GeneralSettingsTabViewModel.OnGeneralChanged uses, because this panel is the second settings
    // surface that has to tell its own write from somebody else's.
    private int _writingThreadId;

    /// <summary>
    /// Whether the calling thread is inside this panel's own write of the general document. Read by
    /// <c>EquipmentTabViewModel</c>'s <c>GeneralChanged</c> handler on the raising thread, before it
    /// posts: <see cref="CommitRow"/> already patches the edited row in place and publishes it, so
    /// answering the store's event as well would recompute the same row a second time on every
    /// keystroke that parses.
    /// </summary>
    internal bool IsOwnWriteThread
        => Environment.CurrentManagedThreadId == Volatile.Read(ref _writingThreadId);

    /// <summary>
    /// Brings a general document written elsewhere onto this panel <strong>without rebuilding its
    /// rows</strong>: the stored document, spec 12.7's "a re-run is still owed" sentence, and each
    /// row's derived zone, site and seen text, which are the figures a completed correlation re-run
    /// moves.
    /// </summary>
    /// <remarks>
    /// Phase 15A carried item 64, as the Task 5c review ruled it. The defect is one sentence going
    /// stale, not the rows: <c>general.phd2_correlation_pending</c> is read only at load, so the
    /// owed sentence stayed up after the re-run that cleared it. <see cref="Reload"/> would answer
    /// it, and would also <c>Rows.Clear()</c> and rebuild every row from the store, which destroys
    /// the editor the user is typing into (every row edit commits through the same
    /// <c>MutateGeneral</c> that raises the event) and orphans the row a failed commit reverts.
    /// This writes no editor and replaces no row instance: <see cref="Phd2ProfileRowViewModel.RefreshResolution"/>
    /// sets only the five derived strings. The row-rebuild path stays where it belongs, on
    /// <see cref="Reload"/>, which the host calls when the rows themselves have changed.
    /// </remarks>
    internal void ApplyGeneral(GeneralSettings general)
    {
        // Written on the UI thread only, like every other write of this field.
        _general = general;
        PendingMessage = PendingMessageFor(general);

        foreach (var row in Rows)
        {
            row.RefreshResolution();
        }
    }

    // Spec 12.7's owed sentence, from the stored flag and whether guide log reading is on. One
    // expression, read by the load's publish and by ApplyGeneral, so the two cannot drift.
    private static string? PendingMessageFor(GeneralSettings general)
        => general.Phd2CorrelationPending
            ? general.Phd2ScanEnabled ? ReRunOwedMessage : ReRunOwedButDisabledMessage
            : null;

    /// <summary>
    /// The map's rewrite on a telescope grouping rename (spec 5.8.1, 12.7). Called by
    /// <c>EquipmentTabViewModel.SaveAsync</c>, off the UI thread, immediately after its own
    /// <c>SaveEquipment</c> succeeds and in the same method: the two writes are not one
    /// transaction, and if this one throws after the equipment save has already landed, the map
    /// simply keeps the old telescope name until the next rename repairs it, which is the accepted
    /// behaviour the report states.
    /// </summary>
    /// <param name="rename">Old telescope name (a former canonical or alias) to the new canonical
    /// name, or null when the rename does not recognise it (left alone by
    /// <see cref="Phd2Profiles.RewriteTelescopes"/>, which is that method's documented contract).
    /// </param>
    /// <param name="isCurrentName">Optional, and a separate delegate precisely because
    /// <paramref name="rename"/>'s null already means "leave it alone": whether a name the rewrite
    /// has settled on is still a canonical equipment name. A name that is not, which is what
    /// deleting a telescope group leaves behind, is unmapped (the entry's telescope goes null, and
    /// the entry is removed if it carries nothing else), never left dangling. Null skips the check
    /// entirely, which is what a caller pinning the rename fold alone wants.</param>
    /// <returns>True when the map actually changed.</returns>
    public bool RewriteTelescopes(Func<string, string?> rename, Func<string, bool>? isCurrentName = null)
    {
        GeneralSettings? next = null;
        var changed = false;
        _mutateGeneral(general =>
        {
            var normalized = Phd2Profiles.Normalize(general.Phd2ProfileMap);
            var rewritten = new Dictionary<string, Phd2ProfileEntry>(
                Phd2Profiles.RewriteTelescopes(general.Phd2ProfileMap, rename), StringComparer.Ordinal);

            if (isCurrentName is not null)
            {
                foreach (var (profile, entry) in rewritten.ToList())
                {
                    if (entry.Telescope is null || isCurrentName(entry.Telescope))
                    {
                        continue;
                    }

                    // The group that carried this name was deleted. Unmapping is the honest state
                    // and it is the same keep-or-remove rule an explicit clear in the picker takes.
                    var cleared = entry with { Telescope = null };
                    if (HasContent(cleared))
                    {
                        rewritten[profile] = cleared;
                    }
                    else
                    {
                        rewritten.Remove(profile);
                    }
                }
            }

            if (Phd2Profiles.SameMap(rewritten, normalized))
            {
                return general;
            }

            changed = true;
            var written = general with { Phd2ProfileMap = Phd2Profiles.ToJson(rewritten) };
            next = written;
            return written;
        });

        if (changed && next is not null)
        {
            // Reload rather than assign _general directly: this method runs off the UI thread
            // (EquipmentTabViewModel.SaveAsync's own background pass) and every read of _general
            // happens on the UI thread from DescribeResolution, so the field is only ever written
            // there too (TRACKING section 6 item 24). Reload's own background pass re-reads the
            // document this write just produced.
            _post(() =>
            {
                StatusMessage = ReRunQueuedMessage;
                Reload();
            });
        }

        return changed;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(Phd2RowEdit? edit)
    {
        if (edit is null)
        {
            return;
        }

        // Cleared here and set again below only by a write that moved the map, so the sentence on
        // screen always describes the most recent save rather than an older one.
        StatusMessage = null;

        // TRACKING section 6 item 13: CanExecute is the affordance, the body is the guard, so a
        // direct Execute from a test or a future non-button call site cannot start a second
        // overlapping write of the same document. The refused edit is CHAINED onto the write in
        // flight rather than dropped: a committed edit that reaches this method is a value the
        // user typed, and silently discarding it stores a coordinate they can see is wrong on
        // screen with nothing saying why (review P1-2).
        if (!CanSave())
        {
            PendingWrite = ChainAsync(PendingWrite, edit);
            await PendingWrite;
            return;
        }

        IsSaving = true;
        var task = Task.Run(() => CommitRow(edit));
        PendingWrite = task;
        try
        {
            // No ConfigureAwait(false). This continuation writes IsSaving, which is an
            // [ObservableProperty] carrying [NotifyCanExecuteChangedFor], so off the dispatcher it
            // is the exact shape of the Phase 14B Clear log crash (TRACKING section 5).
            await task;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // The chain, so two commits arriving back to back both reach the store, in order, and the
    // stored value is the last one. CommitRow reports its own failure and never faults the task it
    // runs in, so the await below is only ever a wait for the previous write's turn.
    private async Task ChainAsync(Task previous, Phd2RowEdit edit)
    {
        try
        {
            await previous;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The PHD2 profile write this edit was chained onto failed");
        }

        await Task.Run(() => CommitRow(edit));
    }

    private bool CanSave() => !IsSaving;

    private void CommitRow(Phd2RowEdit edit)
    {
        GeneralSettings? next = null;
        var changed = false;
        try
        {
            // Claimed around the write only: SettingsStore raises GeneralChanged synchronously on
            // this thread at the end of MutateGeneral, and IsOwnWriteThread is what lets the
            // Equipment tab's follower recognise that raise as this panel's own.
            Volatile.Write(ref _writingThreadId, Environment.CurrentManagedThreadId);
            _mutateGeneral(general =>
            {
                var normalized = Phd2Profiles.Normalize(general.Phd2ProfileMap);
                var map = new Dictionary<string, Phd2ProfileEntry>(normalized, StringComparer.Ordinal);
                var existing = map.TryGetValue(edit.Row.Profile, out var current) ? current : new Phd2ProfileEntry();
                var updated = edit.Edit(existing);

                if (HasContent(updated))
                {
                    map[edit.Row.Profile] = updated;
                }
                else
                {
                    map.Remove(edit.Row.Profile);
                }

                if (Phd2Profiles.SameMap(map, normalized))
                {
                    return general;
                }

                changed = true;
                var written = general with { Phd2ProfileMap = Phd2Profiles.ToJson(map) };
                next = written;
                return written;
            });

            if (changed && next is not null)
            {
                var writtenGeneral = next;
                var storedEntry = Phd2Profiles.Normalize(writtenGeneral.Phd2ProfileMap)
                    .TryGetValue(edit.Row.Profile, out var stored) ? stored : new Phd2ProfileEntry();
                _post(() =>
                {
                    // Written on the UI thread only: every read of _general (DescribeResolution)
                    // happens there too (TRACKING section 6 item 24).
                    _general = writtenGeneral;
                    edit.Row.MarkCommitted(storedEntry);
                    edit.Row.RefreshResolution();
                    ErrorMessage = null;

                    // Spec 12.7: the panel says a re-run has been queued, on the panel's own
                    // knowledge that this write moved the map, and blocks on nothing. The re-run
                    // itself is dispatched from SettingsStore's change event, which fires on the
                    // identical comparison for every writer of the general document.
                    StatusMessage = ReRunQueuedMessage;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving a PHD2 profile mapping failed");
            _post(() =>
            {
                ErrorMessage = "The PHD2 profile mapping could not be saved. See the log for details.";
                edit.Revert();
            });
        }
        finally
        {
            // Released whether the write landed or threw, so a failure cannot leave a pool thread
            // id claimed and make some later raise on the same pooled thread look like this
            // panel's own.
            Volatile.Write(ref _writingThreadId, 0);
        }
    }

    // Whether an entry carries anything at all: a telescope, a timezone or either coordinate. An
    // entry with none of them is removed from the map rather than stored empty (spec 5.8.1),
    // whichever field the edit that emptied it touched.
    private static bool HasContent(Phd2ProfileEntry entry)
        => entry.Telescope is not null || entry.Timezone.Length > 0
            || entry.Latitude is not null || entry.Longitude is not null;

    private void Load()
    {
        IsLoading = true;

        // IsEmpty reads IsLoading, so the empty sentence must come off screen here and not only
        // when Publish lands, or it stays up for the length of a reload (review P3-6).
        OnPropertyChanged(nameof(IsEmpty));
        var generation = ++_generation;
        PendingLoad = Task.Run(() =>
        {
            try
            {
                var general = _loadGeneral();
                var rows = _loadRows();
                var telescopes = SafeKnownTelescopes();
                _post(() => Publish(generation, general, rows, telescopes, failed: false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading the PHD2 profiles panel failed");
                _post(() => Publish(generation, null, [], [], failed: true));
            }
        });
    }

    private IReadOnlyList<string> SafeKnownTelescopes()
    {
        try
        {
            return _knownTelescopes();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the telescope names for the PHD2 panel failed");
            return [];
        }
    }

    private void Publish(
        int generation,
        GeneralSettings? general,
        IReadOnlyList<Phd2ProfileRow> rows,
        IReadOnlyList<string> telescopes,
        bool failed)
    {
        if (generation != _generation)
        {
            return;
        }

        LoadFailed = failed;
        IsLoading = false;

        // Spec 12.7: read at every load, so a re-run owed from a previous run of the application
        // is reported the moment the panel opens rather than only after a save. Cleared on a
        // failed read, which has its own sentence and knows nothing about the flag.
        PendingMessage = !failed && general is not null ? PendingMessageFor(general) : null;

        Rows.Clear();
        if (!failed && general is not null)
        {
            _general = general;
            // Built once for the whole panel; the stored-but-unlisted entry below is per row,
            // because it is that row's own stored value.
            var telescopeOptions = BuildTelescopeOptions(telescopes);
            var timezoneOptions = BuildTimezoneOptions();
            var map = Phd2Profiles.Normalize(general.Phd2ProfileMap);

            foreach (var row in rows)
            {
                var entry = map.TryGetValue(row.Profile, out var stored) ? stored : new Phd2ProfileEntry();
                Rows.Add(new Phd2ProfileRowViewModel(
                    this,
                    row,
                    entry,
                    WithStoredTelescope(telescopeOptions, entry.Telescope),
                    WithStoredTimezone(timezoneOptions, entry.Timezone)));
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private List<Phd2TelescopeOption> BuildTelescopeOptions(IReadOnlyList<string> telescopes)
    {
        var options = new List<Phd2TelescopeOption> { new("", NotMappedLabel) };
        options.AddRange(telescopes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new Phd2TelescopeOption(name, name)));
        return options;
    }

    private List<TimezoneOption> BuildTimezoneOptions()
    {
        var options = new List<TimezoneOption> { new("", InheritTimezoneLabel) };
        options.AddRange(TimezoneOptions.Build(_systemTimezones(), _resolveTimezone, _logger));
        return options;
    }

    // A stored value no picker lists is offered as its own entry rather than silently dropped,
    // which would show the row as unmapped or inheriting while the resolution line directly below
    // it says otherwise, and would rewrite a value the user chose on another machine the moment
    // they touched the picker. LocationTabViewModel.RebuildTimezones is the worked example, and
    // TimezoneOptions.Build's own remark says a caller adds this entry around its list.
    private static IReadOnlyList<Phd2TelescopeOption> WithStoredTelescope(
        List<Phd2TelescopeOption> shared, string? stored)
        => string.IsNullOrEmpty(stored)
            || shared.Any(option => string.Equals(option.Value, stored, StringComparison.Ordinal))
            ? shared
            : [.. shared, new Phd2TelescopeOption(stored, stored + NotInTheEquipmentListSuffix)];

    private static IReadOnlyList<TimezoneOption> WithStoredTimezone(
        List<TimezoneOption> shared, string stored)
        => stored.Length == 0
            || shared.Any(option => string.Equals(option.Id, stored, StringComparison.Ordinal))
            ? shared
            : [.. shared, new TimezoneOption(stored, stored + NotOnThisMachineSuffix)];

    /// <summary>Computes and sets one row's resolution text, called after every load and every
    /// commit so the displayed source never lags the stored map.</summary>
    internal void DescribeResolution(Phd2ProfileRowViewModel row)
    {
        var raw = _general.Phd2ProfileMap;
        var (zone, zoneSource) = Phd2Profiles.ResolveTimezone(
            raw, row.Profile, _general.ObserverTimezone, _resolveTimezone);

        string zoneText;
        var zoneUnset = zoneSource == Phd2ResolutionSource.Unset;
        if (zoneUnset)
        {
            zoneText = UnsetZoneWarning;
        }
        else
        {
            // Phase 14C user ruling U4: a zone reads one way everywhere, offset first. The row's
            // own picker list is already that label for every zone the panel can offer, including
            // a stored zone this machine's database does not list (which keeps the picker's
            // "(not on this machine)" wording), so the sentence reads that Label rather than
            // formatting a second copy or printing the raw Windows id. The one fallback is an
            // observer zone this machine does not list: it is in neither the shared list nor this
            // row's stored entry, and its bare id is then the only name there is.
            zoneText = $"Timezone: {LabelFor(row, zone)} ({DescribeSource(zoneSource)}).";
        }

        // Latitude and longitude resolve independently (spec 7.6): each is reported against its
        // own per-field source rather than the resolved pair's combined source, which is
        // presentation-only over the PAIR and would misreport a profile that carries one
        // coordinate of its own and inherits the other (Phd2Profiles.ResolveSite's own remark).
        var (lat, lon, _) = Phd2Profiles.ResolveSite(
            raw, row.Profile, _general.ObserverLatitude, _general.ObserverLongitude);
        var hasOwnLat = Phd2Profiles.HasOwnLatitude(raw, row.Profile);
        var hasOwnLon = Phd2Profiles.HasOwnLongitude(raw, row.Profile);
        var latSource = hasOwnLat ? Phd2ResolutionSource.Profile
            : lat is not null ? Phd2ResolutionSource.Global : Phd2ResolutionSource.Unset;
        var lonSource = hasOwnLon ? Phd2ResolutionSource.Profile
            : lon is not null ? Phd2ResolutionSource.Global : Phd2ResolutionSource.Unset;

        var latText = lat is { } latValue
            ? $"latitude {latValue.ToString("0.####", CultureInfo.InvariantCulture)} ({DescribeSource(latSource)})"
            : $"latitude not set ({DescribeSource(latSource)})";
        var lonText = lon is { } lonValue
            ? $"longitude {lonValue.ToString("0.####", CultureInfo.InvariantCulture)} ({DescribeSource(lonSource)})"
            : $"longitude not set ({DescribeSource(lonSource)})";

        row.SetResolution(
            zoneText,
            zoneUnset,
            $"Site: {latText}, {lonText}.",
            FormatSeen(row.FirstSeen, row.FirstSeenIsLogClock),
            FormatSeen(row.LastSeen, row.LastSeenIsLogClock));
    }

    private static string LabelFor(Phd2ProfileRowViewModel row, string zoneId)
        => row.TimezoneOptions.FirstOrDefault(option =>
                option.Id.Length > 0 && string.Equals(option.Id, zoneId, StringComparison.Ordinal))
            ?.Label
            ?? zoneId;

    /// <summary>
    /// One "seen" value of spec 12.7's table. A resolved UTC instant is rendered in the display
    /// timezone through the one formatter the frame table and the Activity feed already use, so
    /// the panel's clock reads like the rest of the application. The guide log's own local wall
    /// clock belongs to no zone at all: it is shown as written and marked, never converted through
    /// a zone the session never had and never left blank.
    /// </summary>
    /// <remarks>The basis is the one the READ carried, never the zone that resolves now. Deriving
    /// it from the current resolution made the panel lie the instant a user set a zone on an
    /// unzoned row: the commit path refreshes the resolution and never reloads, so the row still
    /// held the log's wall clock while the formatter had already started treating it as UTC, and
    /// with guide log reading off the re-derive that would have made that true never runs at all
    /// (fix-wave review P2-1).</remarks>
    private string FormatSeen(DateTime seen, bool isLogClock)
    {
        if (isLogClock)
        {
            return Stamp(seen) + UnzonedSeenSuffix;
        }

        return Stamp(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(seen, DateTimeKind.Utc),
            SessionTimeFormat.Resolve(_general.DisplayTimezoneId)));
    }

    private string Stamp(DateTime local)
        => MetricText.Date(DateOnly.FromDateTime(local))
            + " "
            + SessionTimeFormat.FormatLocal(local, _general.Use24HTime);

    private static string DescribeSource(string source) => source switch
    {
        Phd2ResolutionSource.Profile => "this profile's own value",
        Phd2ResolutionSource.Global => "the observer's value",
        _ => "not set",
    };
}
