using System.Text.Json;
using System.Text.Json.Serialization;
using GalactiLog.Core.Scanning;

namespace GalactiLog.Core.Settings;

// Every general.* key from design-spec.md 5.8.1.
public sealed record GeneralSettings
{
    [JsonPropertyName("scan_roots")]
    public string[] ScanRoots { get; init; } = [];

    [JsonPropertyName("scan_filters")]
    public ScanFilterConfig ScanFilters { get; init; } = ScanFilterConfig.Empty;

    /// <summary>Set by the scan filter notice's Review action and by a Library tab save of the
    /// filter block, so a user who keeps the wizard's five rules can still dismiss the notice
    /// (polish wave 1, ruling 1). Read only through <see cref="ScanFilterConfig.ShowsSetupNotice"/>.</summary>
    [JsonPropertyName("scan_filters_reviewed")]
    public bool ScanFiltersReviewed { get; init; }

    [JsonPropertyName("include_calibration")] public bool IncludeCalibration { get; init; }
    [JsonPropertyName("auto_scan_enabled")] public bool AutoScanEnabled { get; init; } = true;
    [JsonPropertyName("auto_scan_interval_minutes")] public int AutoScanIntervalMinutes { get; init; } = 240;
    [JsonPropertyName("watcher_enabled")] public bool WatcherEnabled { get; init; } = true;
    [JsonPropertyName("watcher_debounce_ms")] public int WatcherDebounceMs { get; init; } = 30000;
    [JsonPropertyName("watcher_stability_check_ms")] public int WatcherStabilityCheckMs { get; init; } = 2000;
    [JsonPropertyName("observer_latitude")] public double? ObserverLatitude { get; init; }
    [JsonPropertyName("observer_longitude")] public double? ObserverLongitude { get; init; }
    [JsonPropertyName("observer_name")] public string? ObserverName { get; init; }
    [JsonPropertyName("observer_timezone")] public string ObserverTimezone { get; init; } = "";
    [JsonPropertyName("use_imaging_night")] public bool UseImagingNight { get; init; } = true;
    /// <summary>Empty means the display follows <see cref="ObserverTimezone"/> (polish wave 3,
    /// ruling 1); a reader that formats times goes through <see cref="DisplayTimezoneId"/>.</summary>
    [JsonPropertyName("timezone")] public string Timezone { get; init; } = "";

    /// <summary>The zone times are formatted in: the explicit display timezone, else the observer
    /// timezone, else this machine's. Computed, never written to the document.</summary>
    [JsonIgnore]
    public string DisplayTimezoneId =>
        !string.IsNullOrEmpty(Timezone) ? Timezone
        : !string.IsNullOrEmpty(ObserverTimezone) ? ObserverTimezone
        : TimeZoneInfo.Local.Id;
    [JsonPropertyName("use_24h_time")] public bool Use24HTime { get; init; }

    // Empty means "<app data root>\thumbnails", resolved by AppHost (Task 5) against
    // whatever app data root is actually in effect (the real profile in production, an
    // override root in tests), so this default never hardcodes the real profile path.
    [JsonPropertyName("thumbnail_cache_dir")]
    public string ThumbnailCacheDir { get; init; } = "";

    [JsonPropertyName("thumbnail_width")] public int ThumbnailWidth { get; init; } = 800;
    [JsonPropertyName("preview_resolution")] public int PreviewResolution { get; init; } = 2400;
    [JsonPropertyName("preview_cache_mb")] public int PreviewCacheMb { get; init; } = 2048;

    /// <summary>Spec 5.8.1 and 11.5 (PAR-011). On, a navigation step in the preview modal renders
    /// a fresh preview at <see cref="PreviewResolution"/>; off, the step shows that frame's cached
    /// thumbnail, which is what makes stepping through a few hundred frames usable. The control
    /// lives on the preview modal itself, not on a Settings tab.
    /// <para>
    /// Ships <c>true</c> by coordinator override of 2026-09-17, against the spec table's
    /// <c>false</c>: the port has rendered the full preview on every navigation step since Phase 8
    /// ruling Q18, and a <c>false</c> default would silently invert shipped behaviour. The
    /// checkbox is what turns it off. A document without the key therefore reads <c>true</c> and
    /// needs no migration (spec 5.8).
    /// </para></summary>
    [JsonPropertyName("preview_render_on_navigate")] public bool PreviewRenderOnNavigate { get; init; } = true;
    [JsonPropertyName("default_page_size")] public int DefaultPageSize { get; init; } = 50;
    [JsonPropertyName("activity_retention_days")] public int ActivityRetentionDays { get; init; } = 90;
    [JsonPropertyName("log_level")] public string LogLevel { get; init; } = "Information";

    /// <summary>Spec 5.8.1, PAR-012. The Serilog rolling file sink's <c>retainedFileCountLimit</c>,
    /// read once at startup (design-spec 16.1's "next start" clause, questions.md Q7): a change on
    /// the Diagnostics tab is not applied to the running sink. Range 1 to 3650 (Task 7 departure 3:
    /// this is a day count, not the web's row count).</summary>
    [JsonPropertyName("app_log_retention_days")] public int AppLogRetentionDays { get; init; } = 14;

    /// <summary>Spec 5.8.1, 12.8, PAR-012. Bounds the log viewer's loaded row count and the "copy
    /// all" cap (<c>min(50000, app_log_max_rows)</c>, ruling D3). Does NOT resize
    /// <c>LogRingBuffer</c>'s fixed 500-entry warning ring (task1-report.md departure 2). Range
    /// 1000 to 500000 (departure 3: the web's 5,000,000 would be a memory fault here).</summary>
    [JsonPropertyName("app_log_max_rows")] public int AppLogMaxRows { get; init; } = 50_000;

    /// <summary>Spec 12.6, PAR-017. The instant the Activity feed was last opened, or null on a
    /// profile that has never opened it. <c>DateTimeOffset</c> rather than <c>DateTime</c>: a bare
    /// <c>DateTime</c> round-trips its <c>Kind</c> unreliably through JSON, and this value is
    /// compared against <c>activity_events.timestamp</c>, so the two must be the same kind of
    /// instant. Display state only: it filters nothing and retention pruning ignores it (spec
    /// 12.6).</summary>
    [JsonPropertyName("activity_seen_at")] public DateTimeOffset? ActivitySeenAt { get; init; }
    [JsonPropertyName("theme")] public string Theme { get; init; } = "civil-dusk";
    [JsonPropertyName("text_size")] public string TextSize { get; init; } = "small";
    [JsonPropertyName("content_width")] public string ContentWidth { get; init; } = "extra-wide";
    [JsonPropertyName("setup_complete")] public bool SetupComplete { get; init; }
    [JsonPropertyName("catalogs_loaded_version")] public int CatalogsLoadedVersion { get; init; }

    // Design-spec 12.11's five residency keys, added in one edit by one task (Phase 11 Task 2)
    // so the general document has one shape and one set of defaults rather than five keys
    // appended by four agents. Every one is a bool with a compile-time default, so a document
    // written by an earlier version reads the default and needs no migration (spec 5.8).
    // close_to_tray is the only one that ships on (coordinator ruling Q2): closing the window
    // hides the application in the notification area and it keeps scanning.

    /// <summary>Spec 12.11 behaviour 5. Closing the window hides it instead of ending the
    /// process.</summary>
    [JsonPropertyName("close_to_tray")] public bool CloseToTray { get; init; } = true;

    /// <summary>Spec 12.11 behaviour 6. Minimizing the window hides it.</summary>
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray { get; init; }

    /// <summary>Spec 12.11 behaviour 8. A shortcut in the user's Startup folder launches the
    /// install-root stub with <c>--minimized</c>.</summary>
    [JsonPropertyName("start_with_windows")] public bool StartWithWindows { get; init; }

    /// <summary>Spec 12.11 behaviour 9. The process starts into the tray with no window.
    /// </summary>
    [JsonPropertyName("start_minimized")] public bool StartMinimized { get; init; }

    /// <summary>Spec 12.11 behaviour 10. A scan that finishes while no window is on screen is
    /// reported through the tray tooltip.</summary>
    [JsonPropertyName("notify_on_scan_complete")] public bool NotifyOnScanComplete { get; init; }

    // Design-spec 5.8.1 and 7.6's three PHD2 keys (Phase 15A). Each carries a compile-time
    // default, so a document written by an earlier version reads the default and needs no
    // migration (spec 5.8). None is clamped or validated in SettingsStore: a bool has nothing
    // to clamp, and the profile map's whole coercion is Phd2Profiles.Normalize, a read-path
    // normalisation strictly stronger than a clamp that lives in Core where every reader reaches
    // it (questions-a.md Q1).

    /// <summary>Spec 7.6. Whether a scan discovers and ingests PHD2 guide logs. Ships on: the
    /// guide log is the only source that measures the guiding of an exposure the N.I.N.A. CSV
    /// sidecar said nothing about.</summary>
    [JsonPropertyName("phd2_scan_enabled")] public bool Phd2ScanEnabled { get; init; } = true;

    /// <summary>Spec 7.6. The per-profile telescope, timezone and site mapping, read through
    /// <see cref="Phd2.Phd2Profiles.Normalize"/> and written through
    /// <see cref="Phd2.Phd2Profiles.ToJson"/>.
    /// <para>
    /// <see cref="JsonElement"/> rather than a typed dictionary, deliberately. The key has three
    /// legal stored shapes plus hand-edited junk: the current object form, the legacy
    /// <c>{"Rig A": "Askar 120"}</c> string form that every install predating per-rig timezones
    /// carries, and an entry with only some keys. A typed dictionary throws on the legacy form,
    /// and <c>SettingsStore.Deserialize</c> has no catch, so one malformed key would take down the
    /// whole settings read and with it the application start. <see cref="ExtensionData"/> already
    /// proves the pattern in this record.
    /// </para></summary>
    [JsonPropertyName("phd2_profile_map")] public JsonElement? Phd2ProfileMap { get; init; }

    /// <summary>Spec 5.8.1 and 7.6's "The obligation survives a crash". <b>State, not a
    /// preference</b>: it records that a correlation re-run is owed, so a re-run queued by a
    /// settings save is not forgotten across an application exit. It appears on no Settings tab, no
    /// editor writes it and a user never sees it.
    /// <para>
    /// <c>SettingsStore</c> sets it true in the same save that moves a guiding time input, so the
    /// flag and the change it records are one atomic document; the pass that completes clears it,
    /// and a pass that is cancelled, that fails or that is cut off leaves it true. It is never
    /// itself a guiding input: writing it alone raises no re-run (spec 12.11 item 11a).
    /// </para></summary>
    [JsonPropertyName("phd2_correlation_pending")] public bool Phd2CorrelationPending { get; init; }

    // Design-spec 5.8.1's four Phase 16 keys, serving the Export for stacking page (spec 12.13),
    // added in one edit by one task so the general document has one shape rather than four keys
    // appended by four agents. Each carries a compile-time default of null, so a document written
    // by an earlier version reads as absent and needs no migration (spec 5.8).
    //
    // All four are JsonElement? rather than string, string[] or a typed record, and that is
    // structural rather than a preference. Spec 5.8.1: "None of them has an entry in the write-path
    // validator or the read-path range clamp. Instead each of the four is held on GeneralSettings
    // in a form the deserializer cannot fail on and is read through its own tolerant reader."
    // SettingsStore.Deserialize<T> has no catch, so a stored ["WBPP", 5], or a number where a
    // string is expected, throws inside GetGeneral before any reader could drop anything, which
    // takes the application start down rather than one screen. A typed property cannot answer spec
    // 5.8.1's per-entry drop rules at all. The Document suffix is what lets a grep tell
    // WbppQualityByRigDocument, the stored element, from GalactiLog.Core.Wbpp.WbppQualityByRig,
    // the typed map. Phd2ProfileMap above is in this record for the same reason.
    //
    // The readers are GalactiLog.Core.Wbpp.WbppSettingsRead: DefaultOs, StagingPath, Exclusions
    // and ReadQualityByRig, one per key, each total and each throwing for no input.

    /// <summary>Spec 5.8.1 and 12.13. The script flavour the Generate menu marks as the default,
    /// stored as <c>powershell</c> or <c>bash</c>. Read through
    /// <see cref="Wbpp.WbppSettingsRead.DefaultOs"/>, which answers <c>powershell</c> for an absent
    /// key and for any JSON kind that is not a string; a string outside the set reads as
    /// <c>powershell</c> in turn at <c>ScriptGenerator.ParseOs</c>. Never a detection: nothing on
    /// this port guesses a flavour from the shape of a path.</summary>
    [JsonPropertyName("wbpp_default_os")] public JsonElement? WbppDefaultOsDocument { get; init; }

    /// <summary>Spec 5.8.1 and 12.13. The folder a generated script copies into. Read through
    /// <see cref="Wbpp.WbppSettingsRead.StagingPath"/>; null, absent, empty, whitespace or any
    /// non-string kind all mean <b>unset</b>, and nothing derives a staging root from anything
    /// else, so while it is unset the export page disables Generate and asks for a folder (user
    /// ruling 1 at the 12.13 gate). The two confinement rules are enforced by the editor before the
    /// value is stored, not on read.</summary>
    [JsonPropertyName("wbpp_staging_path")] public JsonElement? WbppStagingPathDocument { get; init; }

    /// <summary>Spec 5.8.1 and 12.13. The folder patterns both script flavours exclude, in order.
    /// Read through <see cref="Wbpp.WbppSettingsRead.Exclusions"/>: a non-array, including an
    /// absent key, reads as the nine defaults, while inside an array a non-string entry, an entry
    /// empty after trimming and an entry carrying a refused character are each dropped and the rest
    /// survive. An array that survives as empty stays empty, because a user who cleared the list
    /// meant it.</summary>
    [JsonPropertyName("wbpp_exclusions")] public JsonElement? WbppExclusionsDocument { get; init; }

    /// <summary>Spec 5.8.1 and 12.13. The export page's quality filter, kept per rig as the web
    /// application keeps it (user ruling 2 at the 12.13 gate), keyed by the port's one rig label
    /// with the literal <c>default</c> as the slot for a selection holding no frame. Entry shape
    /// <c>{enabled, baseline, constraints}</c>. Read through
    /// <see cref="Wbpp.WbppSettingsRead.ReadQualityByRig"/>, where every degradation is local: a
    /// non-object reads as an empty map, a malformed entry reads as that rig's default state
    /// without discarding the other rigs, and one malformed constraint is dropped without
    /// discarding its entry.</summary>
    [JsonPropertyName("wbpp_quality_by_rig")] public JsonElement? WbppQualityByRigDocument { get; init; }

    // The four Phase 21 integration keys (spec 5.8.1 amendment 1a, 12.7, 12.16): three elements a
    // tolerant reader answers for and one bounded scalar clamped on read, the split section 5.8.1
    // already makes.

    /// <summary>Spec 5.8.1, 12.7, 12.16. Keyed by a filter name as the Filters tab spells it, with
    /// a positive integer AstroBin equipment database id as the value. Null means the key is
    /// absent, which reads as an empty map.</summary>
    [JsonPropertyName("astrobin_filter_ids")] public JsonElement? AstroBinFilterIdsDocument { get; init; }

    /// <summary>Spec 5.8.1, 12.7, 12.16. The site's Bortle class, 1 to 9, written into every row of
    /// the AstroBin CSV. Null means unset and leaves the cell blank; a stored value outside the
    /// range is clamped by <c>IntegrationSettings.ReadBortle</c>.</summary>
    [JsonPropertyName("astrobin_bortle")] public int? AstroBinBortle { get; init; }

    /// <summary>Spec 5.8.1, 12.7, 12.16. An array of <c>{name, url, enabled}</c> entries; the
    /// stored order is the order the tab lists and the target page's menu offers. Null means the
    /// key is absent, which reads as an empty list.</summary>
    [JsonPropertyName("nina_instances")] public JsonElement? NinaInstancesDocument { get; init; }

    /// <summary>Spec 5.8.1, 12.7, 12.16. The same entry shape and the same ordering rule as
    /// <see cref="NinaInstancesDocument"/>. Null means the key is absent.</summary>
    [JsonPropertyName("stellarium_instances")] public JsonElement? StellariumInstancesDocument { get; init; }

    /// <summary>Spec 5.8.1 and 11.3. Whether the Sky view may fetch survey images from
    /// alasky.cds.unistra.fr; on by default since Phase 24, and a stored false stays off.</summary>
    [JsonPropertyName("survey_downloads_enabled")] public bool SurveyDownloadsEnabled { get; init; } = true;

    /// <summary>Spec 5.8.1 and 11.3. The stored survey id, raw; every reader passes it through
    /// <see cref="Survey.Surveys.Resolve"/>.</summary>
    [JsonPropertyName("sky_view_survey")] public string SurveyViewSurvey { get; init; } = Survey.Surveys.DefaultId;

    // Preserves any key this version of GalactiLog does not recognize, so a read-modify-write
    // cycle never drops a future key (design-spec 5.8: "an unrecognized key is preserved").
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
