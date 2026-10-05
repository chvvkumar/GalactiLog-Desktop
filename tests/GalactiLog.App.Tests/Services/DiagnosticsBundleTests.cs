using System.Text.Json;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 16.3's eleven-key bundle, written through the scoped export writer. Temp databases come
/// from <c>TestSupport/TempDatabase</c>; every file this class writes lives in a temp folder it
/// created and deletes.
/// </summary>
/// <remarks>
/// The export destination deliberately sits outside the app data root the harness builds, because
/// the bundle is the one sanctioned write outside app data (spec 2.1.1's third row) and a
/// destination inside app data would not exercise that.
/// </remarks>
public class DiagnosticsBundleTests : IDisposable
{
    /// <summary>Spec 16.3's eleven top-level keys, in the order the spec prints them.</summary>
    private static readonly string[] SpecKeys =
    [
        "generated_at", "app", "paths", "database", "settings", "scan", "scan_runs",
        "resolver", "unresolved", "recent_events", "log_tail",
    ];

    private readonly string _appDataRoot = Directory.CreateTempSubdirectory("galactilog-bundle-root-").FullName;
    private readonly string _exportRoot = Directory.CreateTempSubdirectory("galactilog-bundle-out-").FullName;
    private readonly TempDatabase _database;
    private readonly List<IDisposable> _disposables = [];
    private readonly string _logDirectory;

    public DiagnosticsBundleTests()
    {
        _database = new TempDatabase("galactilog-bundle", _appDataRoot);
        _logDirectory = Path.Combine(_appDataRoot, DiagnosticsService.LogDirectoryName);
        Directory.CreateDirectory(_logDirectory);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _database.Dispose();
        foreach (var root in new[] { _appDataRoot, _exportRoot })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }

    private string Destination(string name = "bundle.json") => Path.Combine(_exportRoot, name);

    private sealed record Harness(
        DiagnosticsService Service,
        TempDatabase Database,
        SettingsRepository SettingsRepository,
        string LogDirectory)
    {
        /// <summary>
        /// Writes a stored settings column straight into the row, bypassing <c>SettingsStore</c>.
        /// The accepted test-only exception (TRACKING section 6 item 14): a hand-edited row that
        /// does not parse is exactly what the export has to survive, and no sanctioned writer can
        /// produce one.
        /// </summary>
        public void SeedRaw(Action<UserSettingsRow> mutate)
        {
            var row = SettingsRepository.Load();
            mutate(row);
            SettingsRepository.Save(row);
        }
    }

    private Harness CreateHarness(bool breakAScanRunRead = false)
    {
        var connectionString = new DatabaseConnectionString(_database.ConnectionString);
        var settingsRepository = new SettingsRepository(_database.ConnectionString);
        var settings = new SettingsStore(settingsRepository);
        // Review finding M6: an explicit temp pointer path, never the constructor default, which is
        // the real %APPDATA%GalactiLogdatapath.json.
        var writer = new AppWriter(
            _appDataRoot, dataRootPointerPath: Path.Combine(_appDataRoot, "datapath.json"));
        const string catalogs = @"C:\Program Files\GalactiLog\catalogs";

        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var status = new ScanStatusService(coordinator, action => action());
        var watcher = new WatcherService(
            settings,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            NullLogger<WatcherService>.Instance,
            path => new FakeWatcherSource(path));
        var scheduler = new ScanScheduler(
            settings,
            () => false,
            _ => Task.CompletedTask,
            NullLogger<ScanScheduler>.Instance,
            (_, _) => Task.CompletedTask);

        _disposables.Add(status);
        _disposables.Add(watcher);

        // A scan_runs read that throws, for the "a failed read leaves no file" case: the
        // repository opens READ-ONLY, so a Data Source under a directory that does not exist
        // cannot be created and the read raises SqliteException.
        var scanRunConnectionString = breakAScanRunRead
            ? DatabasePaths.BuildConnectionString(
                Path.Combine(_appDataRoot, "no-such-directory", "missing.db"))
            : _database.ConnectionString;

        var service = new DiagnosticsService(
            new DiagnosticsQuery(connectionString),
            new UnresolvedNamesQuery(connectionString),
            new ScanRunRepository(scanRunConnectionString),
            new ResolverCounters(),
            new LogRingBuffer(),
            writer,
            catalogs,
            () => status,
            () => watcher,
            () => scheduler,
            new GalactiLog.Core.Diagnostics.LogReader(_logDirectory),
            new ActivityQuery(connectionString),
            settingsRepository);

        return new Harness(service, _database, settingsRepository, _logDirectory);
    }

    private JsonDocument Export(Harness harness, string? name = null)
    {
        var destination = Destination(name ?? "bundle.json");
        harness.Service.ExportBundle(destination);
        return JsonDocument.Parse(File.ReadAllText(destination));
    }

    // One entry exactly as spec 16.1's output template writes it, the shape LogReaderTests uses.
    private static string LogEntry(int index)
        => $"2026-09-15 10:00:00.{index:D3} +00:00 [INF] GalactiLog.App.AppHost entry {index}";

    private void SeedLogFile(int entryCount)
    {
        var lines = Enumerable.Range(0, entryCount).Select(LogEntry);
        File.WriteAllText(
            Path.Combine(_logDirectory, "galactilog-20260915.log"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private void SeedScanRuns(int count)
        => _database.Seed(context =>
        {
            for (var index = 0; index < count; index++)
            {
                context.ScanRuns.Add(new ScanRun
                {
                    StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
                    FinishedAt = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).AddMinutes(index),
                    Trigger = "manual",
                    State = "complete",
                    Discovered = index,
                });
            }
        });

    private void SeedActivityEvents(int count)
        => _database.Seed(context =>
        {
            for (var index = 0; index < count; index++)
            {
                context.ActivityEvents.Add(new ActivityEvent
                {
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index),
                    Severity = "info",
                    Category = "scan",
                    EventType = "scan_completed",
                    Message = "event " + index,
                });
            }
        });

    private void SeedUnresolvedFrame(string objectName, int frameCount)
        => _database.Seed(context =>
        {
            for (var index = 0; index < frameCount; index++)
            {
                context.Images.Add(new Image
                {
                    Id = Guid.NewGuid(),
                    FilePath = $@"D:\Astro\{objectName}\{index}.fits",
                    FileName = index + ".fits",
                    ImageType = "LIGHT",
                    ResolvedTargetId = null,
                    RawHeaders = $$"""{"OBJECT":"{{objectName}}"}""",
                });
            }
        });

    private static IReadOnlyList<string> Keys(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name)];

    // ---------------------------------------------------------------- the eleven-key clause

    [Fact]
    public void Bundle_HasExactlyTheElevenSpecKeys_InSpecOrder()
    {
        using var document = Export(CreateHarness());

        // Order as well as membership: spec 16.3 prints them in an order and a support reader
        // scans for them in it.
        Assert.Equal(SpecKeys, Keys(document.RootElement));
    }

    [Fact]
    public void Bundle_App_HasTheSevenSpecKeys()
    {
        using var document = Export(CreateHarness());

        Assert.Equal(
            new[] { "version", "git_sha", "channel", "dotnet", "avalonia", "sqlite", "os" },
            Keys(document.RootElement.GetProperty("app")));
    }

    [Fact]
    public void Bundle_Paths_HasTheSevenSpecKeys()
    {
        // Phase 11 Task 3 appends startup_shortcut and started_minimized to the five spec 17.2
        // keys (spec 16.3).
        using var document = Export(CreateHarness());

        Assert.Equal(
            new[]
            {
                "app_data", "database", "logs", "thumbnails", "catalogs",
                "startup_shortcut", "started_minimized",
            },
            Keys(document.RootElement.GetProperty("paths")));
    }

    [Fact]
    public void Bundle_Database_HasTheSpecKeys()
    {
        using var document = Export(CreateHarness());

        var database = document.RootElement.GetProperty("database");
        // Spec 12.8's ruling F2 adds the guide-log size figure and the method beside the four
        // original keys.
        Assert.Equal(
            new[]
            {
                "file_bytes", "wal_bytes", "page_count", "row_counts",
                "phd2_bytes", "phd2_bytes_method",
            },
            Keys(database));

        // Two literals, not a bool: a support reader must be able to tell a measured figure from
        // an estimate without knowing how the probe works.
        Assert.Contains(
            database.GetProperty("phd2_bytes_method").GetString(),
            new[] { DiagnosticsQuery.MeasuredMethod, DiagnosticsQuery.EstimatedMethod });
        Assert.Equal(JsonValueKind.Object, database.GetProperty("row_counts").ValueKind);

        // The key SET, not its order: row_counts is a dictionary and a dictionary's enumeration
        // order is unspecified (the same rule the page's Publish states).
        Assert.Equal(
            DiagnosticsQuery.RowCountTables.OrderBy(table => table, StringComparer.Ordinal),
            Keys(database.GetProperty("row_counts")).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Bundle_Scan_HasTheFourSpecKeys()
    {
        using var document = Export(CreateHarness());

        var scan = document.RootElement.GetProperty("scan");
        Assert.Equal(
            new[] { "current_state", "current_progress", "watcher_states", "next_scheduled" },
            Keys(scan));

        // Two literals, not a bool.
        Assert.Equal("idle", scan.GetProperty("current_state").GetString());

        // Spec 16.3's scan object does not carry the last run: it is the newest row of
        // scan_runs, which the bundle already carries in full.
        Assert.False(scan.TryGetProperty("last_run", out _));
    }

    [Fact]
    public void Bundle_Resolver_HasTheFiveSpecKeys()
    {
        using var document = Export(CreateHarness());

        Assert.Equal(
            new[] { "cache_positive", "cache_negative", "cache_expired", "hits", "misses" },
            Keys(document.RootElement.GetProperty("resolver")));
    }

    [Fact]
    public void Bundle_UnresolvedRows_HaveObjectNameAndFrameCountOnly()
    {
        SeedUnresolvedFrame("M 31", 3);

        using var document = Export(CreateHarness());

        var rows = document.RootElement.GetProperty("unresolved").EnumerateArray().ToList();
        var row = Assert.Single(rows);

        // UnresolvedNameRow also carries GroupKey; it is not in spec 16.3's shape, so it is not
        // emitted.
        Assert.Equal(new[] { "object_name", "frame_count" }, Keys(row));
        Assert.Equal("M 31", row.GetProperty("object_name").GetString());
        Assert.Equal(3, row.GetProperty("frame_count").GetInt32());
    }

    [Fact]
    public void Bundle_EveryKey_IsSnakeCase()
    {
        SeedUnresolvedFrame("M 31", 1);
        SeedScanRuns(1);
        SeedActivityEvents(1);
        SeedLogFile(1);

        using var document = Export(CreateHarness());

        var offenders = new List<string>();
        WalkKeys(document.RootElement, offenders);

        Assert.Empty(offenders);
    }

    // The settings subtree is exempt: it embeds stored documents verbatim and their key set is
    // the settings schema's business, not this bundle's.
    private static void WalkKeys(JsonElement element, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(property.Name, "^[a-z0-9_]+$"))
                    {
                        offenders.Add(property.Name);
                    }

                    if (property.Name != "settings")
                    {
                        WalkKeys(property.Value, offenders);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkKeys(item, offenders);
                }

                break;
        }
    }

    // ---------------------------------------------------------------- the write path

    [Fact]
    public void ExportBundle_WritesANewFile_AtTheGivenDestination()
    {
        var harness = CreateHarness();
        var destination = Destination();
        Assert.False(File.Exists(destination));

        harness.Service.ExportBundle(destination);

        Assert.True(File.Exists(destination));
        using var document = JsonDocument.Parse(File.ReadAllText(destination));
        Assert.Equal(SpecKeys, Keys(document.RootElement));
    }

    [Fact]
    public void ExportBundle_OverwritesAnExistingFile_WithoutAnApplicationPrompt()
    {
        var harness = CreateHarness();
        var destination = Destination();
        File.WriteAllText(destination, "stale");

        // The save dialog's own overwrite confirmation already happened (spec 12.8 step 3); the
        // application asks nothing of its own and keeps no backup copy.
        harness.Service.ExportBundle(destination);

        using var document = JsonDocument.Parse(File.ReadAllText(destination));
        Assert.Equal(SpecKeys, Keys(document.RootElement));
        Assert.Single(Directory.GetFiles(_exportRoot));
    }

    [Fact]
    public void ExportBundle_WritesUtf8WithoutABom()
    {
        var harness = CreateHarness();
        var destination = Destination();

        harness.Service.ExportBundle(destination);

        var bytes = File.ReadAllBytes(destination);
        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void ExportBundle_WhenASettingsDocumentIsMalformed_StillWritesTheBundle()
    {
        var harness = CreateHarness();

        // The display column, not general: SettingsStore.GetGeneral deserializes general into a
        // C# record and throws on a malformed one, and WatcherService.DescribeRoots reads it
        // inside the snapshot, so a malformed general column fails the whole application long
        // before the export. That is Phase 1 behaviour in a different component and not this
        // bundle's to change; what this case pins is that a column the bundle embeds cannot fail
        // the export.
        harness.SeedRaw(row => row.Display = "{ this is not json");

        using var document = Export(harness);

        var settings = document.RootElement.GetProperty("settings");
        var display = settings.GetProperty("display");
        Assert.True(display.TryGetProperty("parse_error", out var parseError));
        Assert.False(string.IsNullOrWhiteSpace(parseError.GetString()));
        Assert.Equal("{ this is not json", display.GetProperty("raw").GetString());

        // Every other top-level key is intact: an export is a support action and must not fail
        // because one settings column is malformed.
        Assert.Equal(SpecKeys, Keys(document.RootElement));
        Assert.Equal(JsonValueKind.Object, settings.GetProperty("general").ValueKind);
        Assert.Equal(JsonValueKind.Object, settings.GetProperty("graph").ValueKind);
    }

    [Fact]
    public void ExportBundle_Settings_IsAnEmbeddedObjectNotAnEscapedString()
    {
        var harness = CreateHarness();
        harness.SeedRaw(row => row.General = """{"log_level":"Information"}""");

        using var document = Export(harness);

        var general = document.RootElement.GetProperty("settings").GetProperty("general");
        Assert.Equal(JsonValueKind.Object, general.ValueKind);
        Assert.Equal("Information", general.GetProperty("log_level").GetString());
    }

    [Fact]
    public void ExportBundle_AFailedReadLeavesNoFileAtTheDestination()
    {
        var harness = CreateHarness(breakAScanRunRead: true);
        var destination = Destination();

        Assert.ThrowsAny<Exception>(() => harness.Service.ExportBundle(destination));

        // The whole document is built and serialized before BeginExport is called, so a failure
        // in a read leaves nothing at the user's chosen path.
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ExportBundle_LogTail_IsAtMostFiveHundredRawLines_NewestFirst()
    {
        SeedLogFile(600);

        using var document = Export(CreateHarness());

        var tail = document.RootElement.GetProperty("log_tail").EnumerateArray()
            .Select(line => line.GetString())
            .ToList();

        Assert.Equal(DiagnosticsBundle.LogTailCount, tail.Count);
        Assert.Contains("entry 599", tail[0]);
        Assert.Contains("entry 100", tail[^1]);
    }

    [Fact]
    public void ExportBundle_RecentEvents_IsAtMostTwoHundredRows()
    {
        SeedActivityEvents(250);

        using var document = Export(CreateHarness());

        var rows = document.RootElement.GetProperty("recent_events").EnumerateArray().ToList();
        Assert.Equal(DiagnosticsBundle.RecentEventCount, rows.Count);

        // RecentEventCount is written independently of ActivityQuery.MaxLimit on purpose, but
        // ActivityQuery.Page clamps its limit to that cap, so the two are coupled in one
        // direction: a later reduction of the cap below 200 would silently shorten the bundle's
        // recent_events. It must fail here instead (review finding F4).
        Assert.True(
            DiagnosticsBundle.RecentEventCount <= ActivityQuery.MaxLimit,
            $"ActivityQuery.Page clamps to MaxLimit ({ActivityQuery.MaxLimit}), which is now below " +
            $"the bundle's RecentEventCount ({DiagnosticsBundle.RecentEventCount}); spec 16.3 asks " +
            "for the last 200 activity_events rows.");
        Assert.Equal(
            new[]
            {
                "id", "timestamp", "severity", "category", "event_type", "message", "details",
                "target_id", "duration_ms", "parent_id",
            },
            Keys(rows[0]));
    }

    [Fact]
    public void ExportBundle_ScanRuns_IsAtMostFiftyRows_NewestFirst()
    {
        SeedScanRuns(60);

        using var document = Export(CreateHarness());

        var rows = document.RootElement.GetProperty("scan_runs").EnumerateArray().ToList();
        Assert.Equal(DiagnosticsBundle.ScanRunCount, rows.Count);
        Assert.Equal(59, rows[0].GetProperty("discovered").GetInt32());
        Assert.Equal(10, rows[^1].GetProperty("discovered").GetInt32());
        Assert.Equal(
            new[]
            {
                "id", "started_at", "finished_at", "trigger", "state", "discovered", "new_files",
                "changed_files", "completed", "failed", "skipped_calibration", "removed",
                // Spec 5.13's three guide-log counters, between `removed` and `error_text`.
                "phd2_found", "phd2_ingested", "phd2_failed",
                "error_text",
            },
            Keys(rows[0]));
    }

    [Fact]
    public void ExportBundle_OnAFreshInstall_WritesEveryKey_WithEmptyCollectionsNotNull()
    {
        using var document = Export(CreateHarness());
        var root = document.RootElement;

        Assert.Equal(SpecKeys, Keys(root));
        foreach (var key in new[] { "scan_runs", "unresolved", "recent_events", "log_tail" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(key).ValueKind);
            Assert.Empty(root.GetProperty(key).EnumerateArray());
        }

        // DefaultIgnoreCondition is Never, so an absent value is null rather than a missing key:
        // "the field is absent" and "the key is missing" are different diagnoses.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("scan").GetProperty("current_progress").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("scan").GetProperty("next_scheduled").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("generated_at").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("app").GetProperty("version").GetString()));
    }

    [Fact]
    public void ExportBundle_NothingIsRedacted()
    {
        var harness = CreateHarness();
        harness.SeedRaw(row => row.General = """{"scan_roots":["D:\\Astro"]}""");

        using var document = Export(harness);

        // Spec 16.3: there is nothing to redact. Paths are reported verbatim, which is the
        // point of a support bundle. Asserted on the parsed values rather than on the raw text,
        // because JSON escapes a backslash whatever the encoder does.
        var scanRoots = document.RootElement
            .GetProperty("settings").GetProperty("general").GetProperty("scan_roots")
            .EnumerateArray().Select(root => root.GetString()).ToList();
        Assert.Equal([@"D:\Astro"], scanRoots);
        Assert.Equal(_appDataRoot, document.RootElement.GetProperty("paths").GetProperty("app_data").GetString());
    }
}
