using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GalactiLog.Core.Diagnostics;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 16.3's diagnostics bundle: eleven top-level keys, in the order the spec prints them.
/// </summary>
/// <remarks>
/// <para>
/// Every key is an explicit <see cref="JsonPropertyNameAttribute"/>, the house style
/// <c>ExtractedMetadata</c> established, and there is no global naming policy anywhere near this
/// document: a property rename must not be able to silently rename a bundle key. Property
/// declaration order is the serialized key order, which is what
/// <c>DiagnosticsBundleTests.Bundle_HasExactlyTheElevenSpecKeys_InSpecOrder</c> pins.
/// </para>
/// <para>
/// <b>There is nothing to redact</b> (spec 16.3's own sentence). No field is masked, no path is
/// shortened and no key is omitted. If a future setting holds a secret, that is a spec change
/// rather than a silent filter here.
/// </para>
/// <para>
/// Every instant in this document is a <see cref="DateTimeOffset"/> pinned to UTC, so it
/// serializes as ISO-8601 with an explicit offset. A support bundle is read by a person against a
/// log file, and an instant with no zone marker is not an instant.
/// </para>
/// </remarks>
public sealed record DiagnosticsBundle
{
    /// <summary>Spec 16.3's "the last 50 scan_runs rows".</summary>
    public const int ScanRunCount = 50;

    /// <summary>
    /// Spec 16.3's "the last 200 activity_events rows". Equal to <c>ActivityQuery.MaxLimit</c>
    /// today, and deliberately written as its own constant rather than as a reference to it: the
    /// two are equal for unrelated reasons, and a later change to the query's cap must not
    /// silently move what the bundle asks for.
    /// <para>
    /// The independence is one-directional. <c>ActivityQuery.Page</c> clamps its limit to
    /// <c>MaxLimit</c>, so a later reduction of that cap below this number would shorten what the
    /// bundle actually carries. <c>DiagnosticsBundleTests.ExportBundle_RecentEvents_IsAtMostTwoHundredRows</c>
    /// asserts the ordering of the two, so such a reduction fails there rather than silently
    /// shortening a support bundle (review finding F4).
    /// </para>
    /// </summary>
    public const int RecentEventCount = 200;

    /// <summary>Spec 16.3's "the last 500 log lines".</summary>
    public const int LogTailCount = 500;

    /// <summary>Spec 16.3's <c>scan.current_state</c> when a scan is in flight.</summary>
    public const string ScanRunningState = "running";

    /// <summary>Spec 16.3's <c>scan.current_state</c> when no scan is in flight. Two literals,
    /// not a bool: the key names a state and a support reader reads a word.</summary>
    public const string ScanIdleState = "idle";

    [JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; init; }
    [JsonPropertyName("app")] public required BundleApp App { get; init; }
    [JsonPropertyName("paths")] public required BundlePaths Paths { get; init; }
    [JsonPropertyName("database")] public required BundleDatabase Database { get; init; }
    [JsonPropertyName("settings")] public required JsonObject Settings { get; init; }
    [JsonPropertyName("scan")] public required BundleScan Scan { get; init; }
    [JsonPropertyName("scan_runs")] public required IReadOnlyList<BundleScanRun> ScanRuns { get; init; }
    [JsonPropertyName("resolver")] public required BundleResolver Resolver { get; init; }
    [JsonPropertyName("unresolved")] public required IReadOnlyList<BundleUnresolvedName> Unresolved { get; init; }
    [JsonPropertyName("recent_events")] public required IReadOnlyList<BundleActivityEvent> RecentEvents { get; init; }
    [JsonPropertyName("log_tail")] public required IReadOnlyList<string> LogTail { get; init; }

    /// <summary>
    /// Composes the document from one snapshot plus the four reads the snapshot does not carry.
    /// Nothing is read a second way: the snapshot is the same reading spec 12.8's page renders.
    /// </summary>
    /// <param name="snapshot">The one composer's reading (<c>DiagnosticsService.Snapshot</c>).
    /// </param>
    /// <param name="settings">The stored settings document, already embedded by
    /// <see cref="SettingsOf"/>.</param>
    /// <param name="scanRuns">The newest <see cref="ScanRunCount"/> rows, newest first.</param>
    /// <param name="recentEvents">The newest <see cref="RecentEventCount"/> activity rows.</param>
    /// <param name="logTail">The newest <see cref="LogTailCount"/> log entries, newest first.
    /// </param>
    public static DiagnosticsBundle From(
        DiagnosticsSnapshot snapshot,
        JsonObject settings,
        IReadOnlyList<ScanRun> scanRuns,
        IReadOnlyList<ActivityRow> recentEvents,
        IReadOnlyList<LogLine> logTail)
        => new()
        {
            GeneratedAt = snapshot.GeneratedAt,
            App = new BundleApp
            {
                Version = snapshot.App.Version,
                GitSha = snapshot.App.GitSha,
                Channel = snapshot.App.Channel,
                Dotnet = snapshot.App.Dotnet,
                Avalonia = snapshot.App.Avalonia,
                Sqlite = snapshot.App.Sqlite,
                Os = snapshot.App.Os,
            },
            Paths = new BundlePaths
            {
                AppData = snapshot.Paths.AppData,
                Database = snapshot.Paths.Database,
                Logs = snapshot.Paths.Logs,
                Thumbnails = snapshot.Paths.Thumbnails,
                Catalogs = snapshot.Paths.Catalogs,
                StartupShortcut = snapshot.Paths.StartupShortcut,
                StartedMinimized = snapshot.Paths.StartedMinimized,
            },
            Database = new BundleDatabase
            {
                // The database file path is not one of spec 16.3's four database keys: paths
                // carries it, and the snapshot read it once so the two cannot disagree.
                FileBytes = snapshot.Database.FileBytes,
                WalBytes = snapshot.Database.WalBytes,
                PageCount = snapshot.Database.PageCount,
                RowCounts = snapshot.Database.RowCounts,
                Phd2Bytes = snapshot.Database.Phd2Bytes,
                Phd2BytesMethod = snapshot.Database.Phd2BytesMethod,
            },
            Settings = settings,
            Scan = BundleScan.From(snapshot.Scan),
            ScanRuns = [.. scanRuns.Select(BundleScanRun.From)],
            Resolver = new BundleResolver
            {
                CachePositive = snapshot.Resolver.CachePositive,
                CacheNegative = snapshot.Resolver.CacheNegative,
                CacheExpired = snapshot.Resolver.CacheExpired,
                Hits = snapshot.Resolver.Hits,
                Misses = snapshot.Resolver.Misses,
            },
            // UnresolvedNameRow also carries GroupKey. It is not in spec 16.3's shape, so it is
            // not emitted.
            Unresolved = [.. snapshot.Unresolved.Select(row => new BundleUnresolvedName
            {
                ObjectName = row.Name,
                FrameCount = row.FrameCount,
            })],
            RecentEvents = [.. recentEvents.Select(BundleActivityEvent.From)],
            LogTail = [.. logTail.Select(line => line.Raw)],
        };

    /// <summary>
    /// The <c>settings</c> value: the full settings document, embedded as an object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each column of <c>user_settings</c> holds a JSON document as text. Each is parsed and
    /// embedded as JSON rather than emitted as a string: a support bundle whose settings key is
    /// one long backslash-escaped line is unreadable, which would defeat the point of the key.
    /// </para>
    /// <para>
    /// A stored document that does not parse (a hand-edited row) becomes
    /// <c>{ "parse_error": ..., "raw": ... }</c> for that one sub-document and the rest of the
    /// bundle is still written. An export is a support action and must not fail because one
    /// settings column is malformed: that is the exact case a support bundle is most needed for.
    /// </para>
    /// </remarks>
    public static JsonObject SettingsOf(UserSettingsRow row) => new()
    {
        ["general"] = Embed(row.General),
        ["filters"] = Embed(row.Filters),
        ["equipment"] = Embed(row.Equipment),
        ["dismissed_suggestions"] = Embed(row.DismissedSuggestions),
        ["display"] = Embed(row.Display),
        ["graph"] = Embed(row.Graph),
        ["updated_at"] = JsonValue.Create(Utc(row.UpdatedAt)),
    };

    private static JsonNode Embed(string stored)
    {
        try
        {
            return JsonNode.Parse(stored)
                ?? new JsonObject { ["parse_error"] = "the stored document is JSON null", ["raw"] = stored };
        }
        catch (JsonException ex)
        {
            return new JsonObject { ["parse_error"] = ex.Message, ["raw"] = stored };
        }
    }

    /// <summary>A stored instant as an offset instant in UTC. The database columns carry UTC with
    /// an unspecified kind, so the kind is pinned here rather than guessed by the serializer.
    /// </summary>
    internal static DateTimeOffset Utc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static DateTimeOffset? Utc(DateTime? value) => value is { } instant ? Utc(instant) : null;
}

/// <summary>Spec 16.3's <c>app</c> object: seven keys.</summary>
public sealed record BundleApp
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("git_sha")] public required string GitSha { get; init; }
    [JsonPropertyName("channel")] public required string Channel { get; init; }
    [JsonPropertyName("dotnet")] public required string Dotnet { get; init; }
    [JsonPropertyName("avalonia")] public required string Avalonia { get; init; }
    [JsonPropertyName("sqlite")] public required string Sqlite { get; init; }
    [JsonPropertyName("os")] public required string Os { get; init; }
}

/// <summary>Spec 16.3's <c>paths</c> object: five keys from spec 17.2's table, plus the two spec
/// 12.11 adds (Phase 11 Task 3).</summary>
public sealed record BundlePaths
{
    [JsonPropertyName("app_data")] public required string AppData { get; init; }
    [JsonPropertyName("database")] public required string Database { get; init; }
    [JsonPropertyName("logs")] public required string Logs { get; init; }
    [JsonPropertyName("thumbnails")] public required string Thumbnails { get; init; }
    [JsonPropertyName("catalogs")] public required string Catalogs { get; init; }

    /// <summary>Spec 12.11 behaviour 8: <c>present</c>, <c>absent</c> or <c>not applicable</c>
    /// (<c>DiagnosticsService.StartupShortcutPresent</c>/<c>Absent</c>/<c>NotApplicable</c>), or
    /// <c>unknown</c> for a supported seam whose own read of the shortcut failed (Phase 11 Task 3
    /// review, Important 2).</summary>
    [JsonPropertyName("startup_shortcut")] public required string StartupShortcut { get; init; }

    /// <summary>Spec 12.11 behaviour 9: <c>yes</c> or <c>no</c>
    /// (<c>DiagnosticsService.StartedMinimizedYes</c>/<c>No</c>).</summary>
    [JsonPropertyName("started_minimized")] public required string StartedMinimized { get; init; }
}

/// <summary>Spec 16.3's <c>database</c> object: the row-count map plus, since spec 12.8's ruling
/// F2, the guide-log size figure and the method that produced it.</summary>
public sealed record BundleDatabase
{
    [JsonPropertyName("file_bytes")] public required long FileBytes { get; init; }
    [JsonPropertyName("wal_bytes")] public required long WalBytes { get; init; }
    [JsonPropertyName("page_count")] public required long PageCount { get; init; }
    [JsonPropertyName("row_counts")] public required IReadOnlyDictionary<string, long> RowCounts { get; init; }

    /// <summary>Spec 12.8's "PHD2 data size": the bytes the four guide-log tables and their
    /// indexes occupy.</summary>
    [JsonPropertyName("phd2_bytes")] public required long Phd2Bytes { get; init; }

    /// <summary><c>measured</c> or <c>estimated</c>
    /// (<c>DiagnosticsQuery.MeasuredMethod</c>/<c>EstimatedMethod</c>). Two literals, not a bool:
    /// the key names a method and a support reader reads a word.</summary>
    [JsonPropertyName("phd2_bytes_method")] public required string Phd2BytesMethod { get; init; }
}

/// <summary>
/// Spec 16.3's <c>scan</c> object: four keys. A reshape of <c>ScanDiagnostics</c>, not that record
/// serialized, which carries eight fields plus the last run.
/// </summary>
/// <remarks>
/// <c>ScanDiagnostics.LastRun</c> is deliberately absent: the last run is the newest row of
/// <c>scan_runs</c>, which the bundle already carries in full, and duplicating it would let the
/// two disagree after a later edit.
/// </remarks>
public sealed record BundleScan
{
    [JsonPropertyName("current_state")] public required string CurrentState { get; init; }
    [JsonPropertyName("current_progress")] public BundleProgress? CurrentProgress { get; init; }
    [JsonPropertyName("watcher_states")] public required IReadOnlyList<BundleWatcherState> WatcherStates { get; init; }
    [JsonPropertyName("next_scheduled")] public DateTimeOffset? NextScheduled { get; init; }

    public static BundleScan From(ScanDiagnostics scan) => new()
    {
        CurrentState = scan.IsRunning
            ? DiagnosticsBundle.ScanRunningState
            : DiagnosticsBundle.ScanIdleState,
        CurrentProgress = scan.IsRunning
            ? new BundleProgress
            {
                Task = scan.CurrentTask,
                Message = scan.CurrentMessage,
                Percent = scan.CurrentPercent,
                Determinate = scan.HasDeterminatePercent,
            }
            : null,
        WatcherStates = [.. scan.WatcherStates.Select(state => new BundleWatcherState
        {
            Root = state.Root,
            Watching = state.Watching,
            Reachable = state.Reachable,
        })],
        NextScheduled = DiagnosticsBundle.Utc(scan.NextScheduledUtc),
    };
}

/// <summary>Spec 16.3's <c>scan.current_progress</c>: spec 10.4's envelope, or null when idle.
/// </summary>
public sealed record BundleProgress
{
    [JsonPropertyName("task")] public required string Task { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("percent")] public required double Percent { get; init; }
    [JsonPropertyName("determinate")] public required bool Determinate { get; init; }
}

/// <summary>Spec 16.3's <c>scan.watcher_states</c> row (spec 10.7).</summary>
public sealed record BundleWatcherState
{
    [JsonPropertyName("root")] public required string Root { get; init; }
    [JsonPropertyName("watching")] public required bool Watching { get; init; }
    [JsonPropertyName("reachable")] public required bool Reachable { get; init; }
}

/// <summary>One <c>scan_runs</c> row (spec 5.13), in the key order <c>CliDispatcher</c>'s
/// <c>--json</c> payload already prints for the same table.</summary>
public sealed record BundleScanRun
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("started_at")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("finished_at")] public DateTimeOffset? FinishedAt { get; init; }
    [JsonPropertyName("trigger")] public required string Trigger { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("discovered")] public required int Discovered { get; init; }
    [JsonPropertyName("new_files")] public required int NewFiles { get; init; }
    [JsonPropertyName("changed_files")] public required int ChangedFiles { get; init; }
    [JsonPropertyName("completed")] public required int Completed { get; init; }
    [JsonPropertyName("failed")] public required int Failed { get; init; }
    [JsonPropertyName("skipped_calibration")] public required int SkippedCalibration { get; init; }
    [JsonPropertyName("removed")] public required int Removed { get; init; }

    // Spec 5.13's three guide-log counters, between `removed` and `error_text`, which is the
    // column order that table gives them.
    [JsonPropertyName("phd2_found")] public required int Phd2Found { get; init; }
    [JsonPropertyName("phd2_ingested")] public required int Phd2Ingested { get; init; }
    [JsonPropertyName("phd2_failed")] public required int Phd2Failed { get; init; }

    [JsonPropertyName("error_text")] public string? ErrorText { get; init; }

    public static BundleScanRun From(ScanRun run) => new()
    {
        Id = run.Id,
        StartedAt = DiagnosticsBundle.Utc(run.StartedAt),
        FinishedAt = DiagnosticsBundle.Utc(run.FinishedAt),
        Trigger = run.Trigger,
        State = run.State,
        Discovered = run.Discovered,
        NewFiles = run.NewFiles,
        ChangedFiles = run.ChangedFiles,
        Completed = run.Completed,
        Failed = run.Failed,
        SkippedCalibration = run.SkippedCalibration,
        Removed = run.Removed,
        Phd2Found = run.Phd2Found,
        Phd2Ingested = run.Phd2Ingested,
        Phd2Failed = run.Phd2Failed,
        ErrorText = run.ErrorText,
    };
}

/// <summary>Spec 16.3's <c>resolver</c> object: five keys, the three cache figures and the two
/// process counters.</summary>
public sealed record BundleResolver
{
    [JsonPropertyName("cache_positive")] public required long CachePositive { get; init; }
    [JsonPropertyName("cache_negative")] public required long CacheNegative { get; init; }
    [JsonPropertyName("cache_expired")] public required long CacheExpired { get; init; }
    [JsonPropertyName("hits")] public required long Hits { get; init; }
    [JsonPropertyName("misses")] public required long Misses { get; init; }
}

/// <summary>Spec 16.3's <c>unresolved</c> row: two keys and no more.</summary>
public sealed record BundleUnresolvedName
{
    [JsonPropertyName("object_name")] public required string ObjectName { get; init; }
    [JsonPropertyName("frame_count")] public required int FrameCount { get; init; }
}

/// <summary>One <c>activity_events</c> row (spec 5.12), every column the port keeps.</summary>
/// <remarks>
/// <c>details</c> stays the raw string the column holds. It is free-form per event type (spec
/// 10.9) and <c>ActivityQuery</c> states that parsing it is the view layer's business and never
/// the query's; spec 16.3's embed-as-JSON rule is about the settings document, and widening it to
/// a per-row payload would put a parse path for every event shape into the export.
/// </remarks>
public sealed record BundleActivityEvent
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("event_type")] public required string EventType { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("details")] public string? Details { get; init; }
    [JsonPropertyName("target_id")] public Guid? TargetId { get; init; }
    [JsonPropertyName("duration_ms")] public int? DurationMs { get; init; }
    [JsonPropertyName("parent_id")] public int? ParentId { get; init; }

    public static BundleActivityEvent From(ActivityRow row) => new()
    {
        Id = row.Id,
        Timestamp = DiagnosticsBundle.Utc(row.Timestamp),
        Severity = row.Severity,
        Category = row.Category,
        EventType = row.EventType,
        Message = row.Message,
        Details = row.Details,
        TargetId = row.TargetId,
        DurationMs = row.DurationMs,
        ParentId = row.ParentId,
    };
}
