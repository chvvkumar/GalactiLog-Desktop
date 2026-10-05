using System.Text.Json;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.Data.Tests;

public class SettingsStoreTests
{
    // Returns the store alongside its backing TestDatabaseHandle so every test can
    // `using var db = ...` and let TestDatabaseFactory delete the temp .db it created.
    private static (SettingsStore Store, TestDatabaseHandle Db) CreateStore(ILogger? logger = null)
    {
        var db = TestDatabaseFactory.CreateMigratedDatabase();
        return (new SettingsStore(new SettingsRepository(db.ConnectionString), logger), db);
    }

    // Writes user_settings.display as raw text, which is the only way to seed a document this
    // build cannot read: every other route in the solution goes through a serializer that can only
    // produce a readable one.
    private static void SeedDisplayDocument(TestDatabaseHandle db, string json)
    {
        var repository = new SettingsRepository(db.ConnectionString);
        var row = repository.Load();
        row.Display = json;
        repository.Save(row);
    }

    private static string StoredDisplayDocument(TestDatabaseHandle db)
        => new SettingsRepository(db.ConnectionString).Load().Display;

    // A document carrying one custom column list, one non-default dashboard key, one non-default
    // target_page key and one key this build does not know, with one member left to the caller.
    // Each unreadable-member case below puts a token of the wrong kind in that member and asserts
    // the other three survive, which is the half a throw used to take with it.
    private static string DisplayDocumentWith(string member, string stored)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["columns"] = "{\"dashboard\":[\"name\"]}",
            ["dashboard"] = "{\"filter_panel_expanded\":false}",
            ["target_page"] = "{\"frame_list_mode\":\"bad\"}",
            ["a_later_phases_key"] = "7",
        };
        members[member] = stored;

        return "{" + string.Join(",", members.Select(entry => $"\"{entry.Key}\":{entry.Value}")) + "}";
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public void GetGeneral_OnFreshDatabase_ReturnsDocumentedDefaults()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var general = store.GetGeneral();

        Assert.Empty(general.ScanRoots);
        Assert.Empty(general.ScanFilters.IncludePaths);
        Assert.Empty(general.ScanFilters.ExcludePaths);
        Assert.Empty(general.ScanFilters.NameRules);
        Assert.False(general.IncludeCalibration);
        Assert.True(general.AutoScanEnabled);
        Assert.Equal(240, general.AutoScanIntervalMinutes);
        Assert.True(general.WatcherEnabled);
        Assert.Equal(30000, general.WatcherDebounceMs);
        Assert.Equal(2000, general.WatcherStabilityCheckMs);
        Assert.Null(general.ObserverLatitude);
        Assert.Null(general.ObserverLongitude);
        Assert.Null(general.ObserverName);
        Assert.Equal("", general.ObserverTimezone);
        Assert.True(general.UseImagingNight);
        Assert.Equal("", general.Timezone);
        Assert.False(general.Use24HTime);
        Assert.Equal("", general.ThumbnailCacheDir);
        Assert.Equal(800, general.ThumbnailWidth);
        Assert.Equal(2400, general.PreviewResolution);
        Assert.Equal(2048, general.PreviewCacheMb);
        Assert.Equal(50, general.DefaultPageSize);
        Assert.Equal(90, general.ActivityRetentionDays);
        Assert.Equal("Information", general.LogLevel);
        Assert.Equal("civil-dusk", general.Theme);
        Assert.Equal("small", general.TextSize);
        Assert.Equal("extra-wide", general.ContentWidth);
        Assert.False(general.SetupComplete);
        Assert.Equal(0, general.CatalogsLoadedVersion);
    }

    [Fact]
    public void Timezone_Empty_IsTheFirstRunValue_AndSurvivesASave()
    {
        // An empty display timezone means "follow the observer". Failure
        // looks like the fresh row answering this machine's zone id, or a saved "" coming back
        // as anything else.
        var (store, db) = CreateStore();
        using var _ = db;

        Assert.Equal("", store.GetGeneral().Timezone);

        store.SaveGeneral(store.GetGeneral() with { Timezone = "", ObserverTimezone = "Europe/London" });
        var reloaded = store.GetGeneral();

        Assert.Equal("", reloaded.Timezone);
        Assert.Equal("Europe/London", reloaded.ObserverTimezone);
    }

    [Fact]
    public void SaveGeneral_ThenGetGeneral_RoundTrips()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var value = store.GetGeneral() with
        {
            ScanRoots = ["D:\\Astro", "E:\\Astro2"],
            IncludeCalibration = true,
            AutoScanIntervalMinutes = 120,
            ObserverLatitude = 51.5,
            ObserverLongitude = -0.1,
            ObserverName = "Backyard",
            Use24HTime = true,
            LogLevel = "Debug",
            Theme = "custom",
            ScanFiltersReviewed = true,
        };

        store.SaveGeneral(value);
        var reloaded = store.GetGeneral();

        Assert.Equal(value.ScanRoots, reloaded.ScanRoots);
        Assert.True(reloaded.IncludeCalibration);
        Assert.Equal(120, reloaded.AutoScanIntervalMinutes);
        Assert.Equal(51.5, reloaded.ObserverLatitude);
        Assert.Equal(-0.1, reloaded.ObserverLongitude);
        Assert.Equal("Backyard", reloaded.ObserverName);
        Assert.True(reloaded.Use24HTime);
        Assert.Equal("Debug", reloaded.LogLevel);
        Assert.Equal("custom", reloaded.Theme);
        Assert.True(reloaded.ScanFiltersReviewed);
    }

    [Fact]
    public void SaveGeneral_UnrecognizedKeyInStoredJson_SurvivesWriteCycle()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);

        var row = repository.Load();
        row.General = """{"future_key":"keep me"}""";
        repository.Save(row);

        var value = store.GetGeneral();
        store.SaveGeneral(value);

        var raw = repository.Load().General;
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("keep me", doc.RootElement.GetProperty("future_key").GetString());
    }

    [Fact]
    public void SaveGeneral_ScanFilters_RoundTripsTypedShape()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var value = store.GetGeneral() with
        {
            ScanRoots = [@"C:\Astro"],
            ScanFilters = new ScanFilterConfig
            {
                IncludePaths = [@"C:\Astro\Targets"],
                ExcludePaths = [@"C:\Astro\WORK_AREA"],
                NameRules =
                [
                    new NameRule { Id = "r1", Action = "exclude", Type = "glob", Pattern = "cal_*.fits", Target = "file" },
                ],
            },
        };

        store.SaveGeneral(value);
        var reloaded = store.GetGeneral();

        Assert.Equal([@"C:\Astro\Targets"], reloaded.ScanFilters.IncludePaths);
        Assert.Equal([@"C:\Astro\WORK_AREA"], reloaded.ScanFilters.ExcludePaths);
        Assert.Single(reloaded.ScanFilters.NameRules);
        Assert.Equal("cal_*.fits", reloaded.ScanFilters.NameRules[0].Pattern);
    }

    // Review item 4: SaveGeneral must reject an invalid scan_filters config -- via the
    // ScanFilterValidationException -> SettingsValidationException translation in
    // ValidateGeneral -- and leave the previously-persisted general JSON untouched, exactly
    // like every other ValidateGeneral rejection in this file.
    [Theory]
    [InlineData("include_path_outside_every_root")]
    [InlineData("uncompilable_regex")]
    public void SaveGeneral_ScanFiltersInvalid_ThrowsAndDoesNotPersist(string scenario)
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var repository = new SettingsRepository(db.ConnectionString);
        var before = repository.Load().General;

        var scanFilters = scenario switch
        {
            "include_path_outside_every_root" => new ScanFilterConfig { IncludePaths = [@"D:\Other"] },
            "uncompilable_regex" => new ScanFilterConfig
            {
                NameRules = [new NameRule { Id = "r1", Action = "exclude", Type = "regex", Pattern = "(unterminated", Target = "file" }],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var value = store.GetGeneral() with { ScanRoots = [@"C:\Astro"], ScanFilters = scanFilters };

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(value));
        Assert.Equal(before, repository.Load().General);
    }

    [Fact]
    public void SaveGeneral_ScanFilters_UnrecognizedNestedKey_SurvivesWriteCycle()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);

        var row = repository.Load();
        row.General = """{"scan_filters":{"include_paths":[],"exclude_paths":[],"name_rules":[],"future_nested_key":"keep me"}}""";
        repository.Save(row);

        var value = store.GetGeneral();
        store.SaveGeneral(value);

        var raw = repository.Load().General;
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("keep me", doc.RootElement.GetProperty("scan_filters").GetProperty("future_nested_key").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public void SaveGeneral_ActivityRetentionDaysOutOfRange_Throws(int days)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);
        var before = repository.Load().General;
        var value = store.GetGeneral() with { ActivityRetentionDays = days };

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(value));
        Assert.Equal(before, repository.Load().General);
    }

    // Phase 14B Task 7 (PAR-012, questions.md Q12). The two new range checks copy the shape above,
    // and the read-path clamp is a fourth test that does not throw at all.

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public void SaveGeneral_AppLogRetentionDaysOutOfRange_Throws(int days)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);
        var before = repository.Load().General;
        var value = store.GetGeneral() with { AppLogRetentionDays = days };

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(value));
        Assert.Equal(before, repository.Load().General);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(500_001)]
    public void SaveGeneral_AppLogMaxRowsOutOfRange_Throws(int rows)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);
        var before = repository.Load().General;
        var value = store.GetGeneral() with { AppLogMaxRows = rows };

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(value));
        Assert.Equal(before, repository.Load().General);
    }

    [Fact]
    public void AStoredValueBelowTheRange_IsClampedOnRead()
    {
        // A document hand-edited to 9 rows still opens (spec 5.8.1): the clamp lives in the read
        // path, not in a throw, and it is not the save-time validation above.
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);

        var row = repository.Load();
        row.General = """{"activity_retention_days":0,"app_log_retention_days":0,"app_log_max_rows":9}""";
        repository.Save(row);

        var value = store.GetGeneral();

        Assert.Equal(1, value.ActivityRetentionDays);
        Assert.Equal(1, value.AppLogRetentionDays);
        Assert.Equal(1000, value.AppLogMaxRows);
    }

    // Spec 5.8.1's three Phase 18 keys: keywords trimmed with blanks and case-insensitive repeats
    // dropped, a gap outside the seven choices read as 0, the tolerance clamped to 0 to 600.
    [Fact]
    public void TheMosaicKeys_AreNormalizedOnRead_AndDefaultWhenAbsent()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);
        Assert.Equal(["Panel", "P"], store.GetGeneral().MosaicKeywords);
        Assert.Equal((0, 0d), (store.GetGeneral().MosaicCampaignGapDays, store.GetGeneral().MosaicPositionToleranceArcmin));

        var row = repository.Load();
        row.General = """{"mosaic_keywords":[" Panel ","","panel","Tile"],"mosaic_campaign_gap_days":10,"mosaic_position_tolerance_arcmin":900}""";
        repository.Save(row);

        var value = store.GetGeneral();
        Assert.Equal(["Panel", "Tile"], value.MosaicKeywords);
        Assert.Equal((0, 600d), (value.MosaicCampaignGapDays, value.MosaicPositionToleranceArcmin));
    }

    [Fact]
    public void SaveGeneral_AMosaicGapOrToleranceOutOfRange_Throws()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(new GeneralSettings { MosaicCampaignGapDays = 10 }));
        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(new GeneralSettings { MosaicPositionToleranceArcmin = -1 }));
        store.SaveGeneral(new GeneralSettings { MosaicCampaignGapDays = 30, MosaicPositionToleranceArcmin = 12.5 });
        Assert.Equal((30, 12.5), (store.GetGeneral().MosaicCampaignGapDays, store.GetGeneral().MosaicPositionToleranceArcmin));
    }

    [Fact]
    public void AStoredValueAboveTheRange_IsClampedOnRead()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var repository = new SettingsRepository(db.ConnectionString);
        var store = new SettingsStore(repository);

        var row = repository.Load();
        row.General = """{"activity_retention_days":99999,"app_log_retention_days":99999,"app_log_max_rows":99999999}""";
        repository.Save(row);

        var value = store.GetGeneral();

        Assert.Equal(3650, value.ActivityRetentionDays);
        Assert.Equal(3650, value.AppLogRetentionDays);
        Assert.Equal(500_000, value.AppLogMaxRows);
    }

    [Theory]
    [InlineData(91)]
    [InlineData(-91)]
    public void SaveGeneral_ObserverLatitudeOutOfRange_Throws(double latitude)
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var value = store.GetGeneral() with { ObserverLatitude = latitude };

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(value));
    }

    [Fact]
    public void SaveGeneral_ThumbnailCacheDirIsDriveRoot_Throws()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var value = store.GetGeneral() with { ThumbnailCacheDir = "C:\\" };

        Assert.Throws<SettingsValidationException>(() => store.SaveGeneral(value));
    }

    [Fact]
    public void GetDisplay_OnFreshDatabase_ReturnsDocumentedDefaults()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var display = store.GetDisplay();

        Assert.True(display.Groups["quality"].Enabled);
        Assert.True(display.Groups["guiding"].Enabled);
        Assert.False(display.Groups["adu"].Enabled);
        Assert.False(display.Groups["focuser"].Enabled);
        Assert.False(display.Groups["weather"].Enabled);
        Assert.False(display.Groups["mount"].Enabled);
        Assert.All(display.Groups.Values, group => Assert.All(group.Fields.Values, Assert.True));

        Assert.Equal(
            new[] { "name", "designation", "palette", "integration", "equipment", "last_session" },
            display.Columns["dashboard"]);
        Assert.Equal(
            new[] { "time", "file_name", "filter_used", "exposure_time", "median_hfr", "eccentricity", "fwhm", "detected_stars" },
            display.Columns["frames"]);
    }

    // Phase 18 Task 4, spec 5.8.2: the mosaics table's column list and sort.
    [Fact]
    public void GetDisplay_OnFreshDatabase_HasTheMosaicsColumnsAndSortDefaults()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var display = store.GetDisplay();

        Assert.Equal(
            new[] { "name", "panels", "integration", "frames", "date_range" },
            display.ColumnsFor(DisplaySettings.MosaicsTableId));
        Assert.Equal(new TableSort { Key = "name", Ascending = true }, display.MosaicsSort);
    }

    [Fact]
    public void SaveDisplay_TheMosaicsSortAndColumns_RoundTrip()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var display = store.GetDisplay().WithSort(DisplaySettings.MosaicsTableId, new TableSort { Key = "frames", Ascending = false });
        store.SaveDisplay(display with
        {
            Columns = new Dictionary<string, string[]>(display.Columns) { ["mosaics"] = ["name", "frames", "custom_owner"] },
        });

        Assert.Contains("\"sort\":{\"mosaics\":{\"key\":\"frames\",\"ascending\":false}}", StoredDisplayDocument(db), StringComparison.Ordinal);
        var reloaded = store.GetDisplay();
        Assert.Equal(new TableSort { Key = "frames", Ascending = false }, reloaded.MosaicsSort);
        Assert.Equal(new[] { "name", "frames", "custom_owner" }, reloaded.ColumnsFor(DisplaySettings.MosaicsTableId));
    }

    // Spec 5.8.2: a stored key outside the five, a custom slug included, reads as the default; a
    // document with no sort object takes the default too, so no migration is needed.
    [Theory]
    [InlineData("""{"sort":{"mosaics":{"key":"custom_owner","ascending":false}}}""")]
    [InlineData("""{"sort":{"mosaics":{"key":"nonsense"}}}""")]
    [InlineData("""{"sort":{}}""")]
    [InlineData("""{}""")]
    public void GetDisplay_AnUnknownMosaicsSortKey_ReadsAsTheDefault(string stored)
    {
        var (store, db) = CreateStore();
        using var _ = db;
        SeedDisplayDocument(db, stored);

        Assert.Equal(new TableSort(), store.GetDisplay().MosaicsSort);
    }

    [Fact]
    public void SaveDisplay_PreservesAnUnknownTargetPageKey()
    {
        // Spec 5.8: an unrecognized key survives a write, inside the nested object as well as at
        // the top level, so a profile written by a later build is not truncated by this one.
        var (store, db) = CreateStore();
        using var _ = db;

        var seeded = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"target_page\":{\"frame_list_mode\":\"bad\",\"a_later_phases_key\":\"kept\"}}")!;
        store.SaveDisplay(seeded);

        var reloaded = store.GetDisplay();
        Assert.Equal("bad", reloaded.TargetPage.FrameListMode);
        Assert.NotNull(reloaded.TargetPage.ExtensionData);
        Assert.Equal("kept", reloaded.TargetPage.ExtensionData!["a_later_phases_key"].GetString());

        store.SaveDisplay(reloaded with
        {
            TargetPage = reloaded.TargetPage with { GradingBaseline = "rig" },
        });

        var written = store.GetDisplay().TargetPage;
        Assert.Equal("rig", written.GradingBaseline);
        Assert.Equal("kept", written.ExtensionData!["a_later_phases_key"].GetString());
    }

    [Fact]
    public void SaveDisplay_TheLanesHeights_RoundTripPerLayoutBesideTheOtherKeys()
    {
        // A failure looks like a height lost, written under another name, shared by two layouts, or
        // a later build's key under the layout or beside it dropped.
        var (store, db) = CreateStore();
        using var _ = db;

        store.SaveDisplay(JsonSerializer.Deserialize<DisplaySettings>(
            "{\"target_page\":{\"a_later_phases_key\":\"kept\","
            + "\"layouts\":{\"modes\":{\"lanes_height\":310.5,\"later\":1},\"bench\":{}}}}")!);

        Assert.Contains("\"lanes_height\":310.5", StoredDisplayDocument(db), StringComparison.Ordinal);
        var reloaded = store.GetDisplay().TargetPage;
        Assert.Equal(310.5, reloaded.Layouts["modes"].LanesHeight);
        Assert.Equal(1, reloaded.Layouts["modes"].ExtensionData!["later"].GetInt32());
        Assert.Null(reloaded.Layouts["bench"].LanesHeight);
        Assert.Equal("kept", reloaded.ExtensionData!["a_later_phases_key"].GetString());
    }

    [Fact]
    public void GetDisplay_ANullLayoutEntry_LoadsAndTheOtherLayoutsSurvive()
    {
        // A failure looks like a null entry losing the document or the sibling layout's height.
        var (store, db) = CreateStore();
        using var _ = db;

        store.SaveDisplay(JsonSerializer.Deserialize<DisplaySettings>(
            "{\"target_page\":{\"layouts\":{\"modes\":null,\"bench\":{\"lanes_height\":410}}}}")!);

        var reloaded = store.GetDisplay().TargetPage;
        Assert.Equal(410, reloaded.Layouts["bench"].LanesHeight);
        Assert.Null(reloaded.Layouts.GetValueOrDefault("modes"));
    }

    // ---- a display document this build cannot read in full (spec 5.8.2's hand-edit rule) --------
    //
    // GetDisplay used to be a bare Deserialize<DisplaySettings>, so one member of the wrong kind
    // raised JsonException and the whole document was lost: the profile's columns, its dashboard
    // panel, its target_page disclosures and its Analysis keys went with it, and because the
    // startup read of this document is what raised the exception, the application did not start at
    // all. The rule is one member's cost is that member alone.

    [Theory]
    [InlineData("5")]
    [InlineData("\"open\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[{\"frame_list_mode\":\"bad\"}]")]
    public void GetDisplay_TargetPageOfTheWrongKind_AnswersItsDefaults_AndKeepsTheRest(string stored)
    {
        var (store, db) = CreateStore();
        using var _ = db;
        SeedDisplayDocument(db, DisplayDocumentWith("target_page", stored));

        var display = store.GetDisplay();

        // The defaults, exactly as a document with no target_page key at all reads.
        Assert.Equal("session", display.TargetPage.GradingBaseline);
        Assert.Equal("paths", display.TargetPage.FrameListFormat);
        Assert.Equal("good", display.TargetPage.FrameListMode);

        Assert.Equal(["name"], display.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.False(display.Dashboard.FilterPanelExpanded);
        Assert.Equal("correlation", display.Analysis.Tab);
        Assert.Equal(7, display.ExtensionData!["a_later_phases_key"].GetInt32());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"wide\"")]
    [InlineData("null")]
    [InlineData("[]")]
    public void GetDisplay_DashboardOfTheWrongKind_AnswersItsDefaults_AndKeepsTheRest(string stored)
    {
        var (store, db) = CreateStore();
        using var _ = db;
        SeedDisplayDocument(db, DisplayDocumentWith("dashboard", stored));

        var display = store.GetDisplay();

        Assert.False(display.Dashboard.FilterPanelExpanded);
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, display.Dashboard.FilterPanelWidth);

        Assert.Equal(["name"], display.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.Equal("bad", display.TargetPage.FrameListMode);
        Assert.Equal(7, display.ExtensionData!["a_later_phases_key"].GetInt32());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"name\"")]
    [InlineData("null")]
    [InlineData("[\"name\"]")]
    public void GetDisplay_ColumnsOfTheWrongKind_AnswersTheDefaultLists_AndKeepsTheRest(string stored)
    {
        var (store, db) = CreateStore();
        using var _ = db;
        SeedDisplayDocument(db, DisplayDocumentWith("columns", stored));

        var display = store.GetDisplay();

        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            display.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.FramesTableId],
            display.ColumnsFor(DisplaySettings.FramesTableId));

        Assert.Equal("bad", display.TargetPage.FrameListMode);
        Assert.False(display.Dashboard.FilterPanelExpanded);
        Assert.Equal(7, display.ExtensionData!["a_later_phases_key"].GetInt32());
    }

    [Fact]
    public void GetDisplay_AKeyInsideAMember_CostsThatKeyAlone()
    {
        // The refused path is the key, not the object that holds it, so the member's other keys
        // are still read. This is what makes the guard finer than a converter per member would be.
        var (store, db) = CreateStore();
        using var _ = db;
        SeedDisplayDocument(
            db,
            DisplayDocumentWith(
                "dashboard",
                "{\"filter_panel_expanded\":false,\"filter_panel_width\":\"as wide as it goes\"}"));

        var display = store.GetDisplay();

        Assert.False(display.Dashboard.FilterPanelExpanded);
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, display.Dashboard.FilterPanelWidth);
        Assert.Equal("bad", display.TargetPage.FrameListMode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("\"display\"")]
    [InlineData("{\"columns\":")]
    [InlineData("")]
    [InlineData("not json at all")]
    public void GetDisplay_ADocumentThatIsNotAReadableObject_AnswersTheDefaults(string stored)
    {
        // The literal null is the row System.Text.Json handles silently and worst: it throws
        // nothing and hands back a null document, so every caller's first member read is a null
        // reference.
        var logger = new RecordingLogger();
        var (store, db) = CreateStore(logger);
        using var _ = db;
        SeedDisplayDocument(db, stored);

        var display = store.GetDisplay();

        Assert.True(display.Groups["quality"].Enabled);
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            display.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.True(display.TargetPage.FrameListIncludeUnmeasured);
        Assert.False(display.Dashboard.FilterPanelExpanded);
        Assert.Equal("correlation", display.Analysis.Tab);

        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void GetDisplay_LogsOnce_NamingWhatItCouldNotRead()
    {
        var logger = new RecordingLogger();
        var (store, db) = CreateStore(logger);
        using var _ = db;
        SeedDisplayDocument(db, DisplayDocumentWith("target_page", "5"));

        store.GetDisplay();

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("$.target_page", warning, StringComparison.Ordinal);

        // A document that reads in full says nothing at all.
        logger.Warnings.Clear();
        store.SaveDisplay(store.GetDisplay());
        logger.Warnings.Clear();
        store.GetDisplay();
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void SaveDisplay_AfterAPartlyUnreadableRead_KeepsEveryReadableMember()
    {
        // The data-loss half. Every writer of this document is a load-modify-save
        // (DisplayColumnWriter.Run reads inside its own queued write), so what the read answers is
        // what the next save persists: a member the read could not answer must not take the
        // readable ones down with it.
        var (store, db) = CreateStore();
        using var _ = db;
        SeedDisplayDocument(db, DisplayDocumentWith("target_page", "5"));

        var read = store.GetDisplay();
        store.SaveDisplay(read with { TargetPage = read.TargetPage with { GradingBaseline = "rig" } });

        var written = store.GetDisplay();
        Assert.Equal(["name"], written.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.False(written.Dashboard.FilterPanelExpanded);
        Assert.Equal("rig", written.TargetPage.GradingBaseline);
        Assert.Equal(7, written.ExtensionData!["a_later_phases_key"].GetInt32());

        // What the save does NOT keep is the raw text of the member that could not be read: it is
        // replaced by the object this build writes, and the warning logged at the read is the only
        // record of it.
        Assert.DoesNotContain("\"target_page\":5", StoredDisplayDocument(db), StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_Display_Graph_Filters_Equipment_DismissedSuggestions()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var display = store.GetDisplay() with
        {
            Columns = new Dictionary<string, string[]> { ["dashboard"] = ["name"] },
        };
        store.SaveDisplay(display);
        Assert.Equal(new[] { "name" }, store.GetDisplay().Columns["dashboard"]);

        var graph = store.GetGraph() with { EnabledMetrics = ["hfr"], DefaultChartSessions = 3 };
        store.SaveGraph(graph);
        var reloadedGraph = store.GetGraph();
        Assert.Equal(new[] { "hfr" }, reloadedGraph.EnabledMetrics);
        Assert.Equal(3, reloadedGraph.DefaultChartSessions);

        var filters = new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#ff0000", Aliases = ["H-alpha", "Halpha"] },
        };
        store.SaveFilters(filters);
        var reloadedFilters = store.GetFilters();
        Assert.Equal("#ff0000", reloadedFilters["Ha"].Color);
        Assert.Equal(new[] { "H-alpha", "Halpha" }, reloadedFilters["Ha"].Aliases);

        var equipment = new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["ZWO ASI2600MM Pro"] },
            },
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new EquipmentItemSettings { Aliases = ["8in RC"] },
            },
        };
        store.SaveEquipment(equipment);
        var reloadedEquipment = store.GetEquipment();
        Assert.Equal(new[] { "ZWO ASI2600MM Pro" }, reloadedEquipment.Cameras["ASI2600MM"].Aliases);
        Assert.Equal(new[] { "8in RC" }, reloadedEquipment.Telescopes["RC8"].Aliases);

        var dismissed = new List<List<string>> { new() { "M31", "Andromeda Galaxy" } };
        store.SaveDismissedSuggestions(dismissed);
        var reloadedDismissed = store.GetDismissedSuggestions();
        Assert.Single(reloadedDismissed);

        // Phase 9 Task 6: each inner group is sorted ordinally at the store, matching the web's
        // own [sorted(group) for group in payload]. The round trip is still a round trip; the
        // stored order is now the normalized one. See SaveDismissedSuggestions_SortsEachInnerGroup.
        Assert.Equal(new[] { "Andromeda Galaxy", "M31" }, reloadedDismissed[0]);
    }

    [Fact]
    public void SaveFilters_RaisesAliasSourcesChanged()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var raised = 0;
        store.AliasSourcesChanged += (_, _) => raised++;

        store.SaveFilters(new Dictionary<string, FilterSetting>());

        Assert.Equal(1, raised);
    }

    [Fact]
    public void SaveEquipment_RaisesAliasSourcesChanged()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var raised = 0;
        store.AliasSourcesChanged += (_, _) => raised++;

        store.SaveEquipment(new EquipmentSettings());

        Assert.Equal(1, raised);
    }

    [Fact]
    public void SaveDisplay_DoesNotRaiseAliasSourcesChanged()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var raised = 0;
        store.AliasSourcesChanged += (_, _) => raised++;

        store.SaveDisplay(store.GetDisplay());

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SaveGeneral_RaisesGeneralChanged_WithSavedValue()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        GeneralSettings? received = null;
        var raised = 0;
        store.GeneralChanged += (_, value) =>
        {
            raised++;
            received = value;
        };
        var value = store.GetGeneral() with { LogLevel = "Warning" };

        store.SaveGeneral(value);

        Assert.Equal(1, raised);
        Assert.Equal(value, received);
    }

    // Phase 6 review finding 3. user_settings is a single row holding six documents and every
    // Save* is a whole-row load-modify-save, so two independent background write chains
    // (GraphSettingsWriter's and Task 5's DisplayColumnWriter's) each read the row, replace their
    // own column, and write the whole row back. Without the lock inside SettingsStore the later
    // save reverts the earlier one's document, and the loser is whichever chain lost the race.
    //
    // Each thread owns one document and is its only writer, so after its own save a read-back can
    // only disagree if the other thread wrote a stale copy of the row over it. That makes a
    // mismatch a true lost update with no false positives, rather than a race on which thread
    // finishes last.
    [Fact]
    public async Task SaveGraph_And_SaveDisplay_Interleaved_NeitherDocumentIsLost()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        const int Iterations = 150;
        var lostUpdates = 0;

        var graphWriter = Task.Run(() =>
        {
            for (var index = 1; index <= Iterations; index++)
            {
                store.SaveGraph(store.GetGraph() with { DefaultChartSessions = index });
                if (store.GetGraph().DefaultChartSessions != index)
                {
                    Interlocked.Increment(ref lostUpdates);
                }
            }
        });
        var displayWriter = Task.Run(() =>
        {
            for (var index = 1; index <= Iterations; index++)
            {
                var expected = $"col{index}";
                store.SaveDisplay(store.GetDisplay() with
                {
                    Columns = new Dictionary<string, string[]>
                    {
                        [DisplaySettings.DashboardTableId] = [expected],
                    },
                });
                if (store.GetDisplay().Columns[DisplaySettings.DashboardTableId] is not [var actual]
                    || actual != expected)
                {
                    Interlocked.Increment(ref lostUpdates);
                }
            }
        });
        await Task.WhenAll(graphWriter, displayWriter).WaitAsync(TimeSpan.FromSeconds(120));

        Assert.Equal(0, lostUpdates);
        Assert.Equal(Iterations, store.GetGraph().DefaultChartSessions);
        Assert.Equal([$"col{Iterations}"], store.GetDisplay().Columns[DisplaySettings.DashboardTableId]);
    }

    // ---- MutateGeneral (Phase 9 Task 5 review escalation) -------------------------------------
    //
    // GetGeneral and SaveGeneral each take the gate separately, so a caller that reads, modifies
    // and writes holds nothing across the window between them: two writers of the general document
    // can lose one another's update even though every individual access is serialised. The Library
    // tab is the first such writer and Task 6's tabs and Task 9's wizard are the next four, so the
    // fix is one method on the store rather than a convention every tab has to remember.

    [Fact]
    public void MutateGeneral_AppliesTheMutation_AndReturnsWhatWasWritten()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var written = store.MutateGeneral(general => general with
        {
            ScanRoots = [@"C:\Astro\Captures"],
            IncludeCalibration = true,
        });

        Assert.Equal([@"C:\Astro\Captures"], written.ScanRoots);
        Assert.True(written.IncludeCalibration);

        var reread = store.GetGeneral();
        Assert.Equal([@"C:\Astro\Captures"], reread.ScanRoots);
        Assert.True(reread.IncludeCalibration);

        // Everything the mutation did not touch is unchanged, because the mutation ran against the
        // stored document rather than against a fresh one.
        Assert.Equal(240, reread.AutoScanIntervalMinutes);
        Assert.True(reread.WatcherEnabled);
    }

    [Fact]
    public void MutateGeneral_ReadsTheStoredDocument_NotASnapshotTheCallerHeld()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var stale = store.GetGeneral();

        store.SaveGeneral(stale with { ObserverName = "written by someone else" });

        // The caller still holds `stale`, from before that write. MutateGeneral hands the
        // mutation the document as it is now, so the other writer's key survives.
        store.MutateGeneral(general => general with { IncludeCalibration = true });

        var reread = store.GetGeneral();
        Assert.Equal("written by someone else", reread.ObserverName);
        Assert.True(reread.IncludeCalibration);
    }

    [Fact]
    public void MutateGeneral_RaisesGeneralChangedOnce_WithTheWrittenDocument()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        var raised = new List<GeneralSettings>();
        store.GeneralChanged += (_, general) => raised.Add(general);

        var written = store.MutateGeneral(general => general with { ThumbnailWidth = 1200 });

        var only = Assert.Single(raised);
        Assert.Same(written, only);
        Assert.Equal(1200, only.ThumbnailWidth);
    }

    [Fact]
    public void MutateGeneral_ThatFailsValidation_WritesNothing_AndRaisesNothing()
    {
        var (store, db) = CreateStore();
        using var _ = db;
        store.SaveGeneral(store.GetGeneral() with { ScanRoots = [@"C:\Astro\Captures"] });
        var raised = 0;
        store.GeneralChanged += (_, _) => raised++;

        // An include path outside every scan root: ScanFilterConfig.Validate refuses it, and
        // SettingsStore translates that into SettingsValidationException. Validation runs inside
        // the critical section, so nothing reaches the row.
        Assert.Throws<SettingsValidationException>(() => store.MutateGeneral(general => general with
        {
            ScanFilters = general.ScanFilters with { IncludePaths = [@"E:\Somewhere\Else"] },
        }));

        Assert.Equal(0, raised);
        Assert.Empty(store.GetGeneral().ScanFilters.IncludePaths);
        Assert.Equal([@"C:\Astro\Captures"], store.GetGeneral().ScanRoots);
    }

    [Fact]
    public void MutateGeneral_ThatThrowsFromTheMutation_WritesNothing_AndLeavesTheGateUsable()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        Assert.Throws<InvalidOperationException>(
            () => store.MutateGeneral(_ => throw new InvalidOperationException("mutation failed")));

        Assert.Equal(240, store.GetGeneral().AutoScanIntervalMinutes);

        // The gate was released, so the store still works.
        store.MutateGeneral(general => general with { AutoScanIntervalMinutes = 720 });
        Assert.Equal(720, store.GetGeneral().AutoScanIntervalMinutes);
    }

    [Fact]
    public async Task MutateGeneral_ConcurrentMutators_SerialiseWithNoLostUpdate()
    {
        const int Iterations = 60;
        var (store, db) = CreateStore();
        using var _ = db;

        // Two writers of the SAME document, each doing a read-modify-write of a different key.
        // This is the case the per-document gate alone does not cover: with GetGeneral plus
        // SaveGeneral, each writer's read is separated from its write by a window the other can
        // land in, and the later write reverts the earlier one's key.
        var widthWriter = Task.Run(() =>
        {
            for (var i = 1; i <= Iterations; i++)
            {
                store.MutateGeneral(general => general with { ThumbnailWidth = general.ThumbnailWidth + 1 });
            }
        });

        var retentionWriter = Task.Run(() =>
        {
            for (var i = 1; i <= Iterations; i++)
            {
                store.MutateGeneral(general => general with
                {
                    ActivityRetentionDays = general.ActivityRetentionDays + 1,
                });
            }
        });

        await Task.WhenAll(widthWriter, retentionWriter).WaitAsync(TimeSpan.FromSeconds(120));

        var final = store.GetGeneral();
        Assert.Equal(800 + Iterations, final.ThumbnailWidth);
        Assert.Equal(90 + Iterations, final.ActivityRetentionDays);
    }

    // ---- DisplayChanged (Phase 9 Task 6, FIXER item 7) ----------------------------------------
    //
    // SaveGeneral and SaveFilters/SaveEquipment raise events; SaveDisplay raised nothing. Phase 6
    // reads display.groups once, by value, and passes it into each frame table, which
    // was correct while nothing in the process could write it. The Settings Display tab is the
    // first writer, so a metric group turned off would not hide its columns until the next start.

    [Fact]
    public void SaveDisplay_RaisesDisplayChanged()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var raised = new List<DisplaySettings>();
        store.DisplayChanged += (_, display) => raised.Add(display);

        var next = store.GetDisplay() with
        {
            Columns = new Dictionary<string, string[]> { ["frames"] = ["time", "file_name"] },
        };
        store.SaveDisplay(next);

        var carried = Assert.Single(raised);
        Assert.Equal(["time", "file_name"], carried.Columns["frames"]);
        Assert.Equal(["time", "file_name"], store.GetDisplay().Columns["frames"]);
    }

    [Fact]
    public void SaveDisplay_RaisesDisplayChangedOutsideTheWriteGate()
    {
        // The store's own comment on _writeGate says change events are raised outside the lock,
        // because a subscriber does work of its own. A subscriber that reads the document back
        // would deadlock on a re-entrant take of a non-recursive Lock if this were raised inside.
        var (store, db) = CreateStore();
        using var _ = db;

        DisplaySettings? readBack = null;
        store.DisplayChanged += (_, _) => readBack = store.GetDisplay();

        store.SaveDisplay(store.GetDisplay() with
        {
            Columns = new Dictionary<string, string[]> { ["dashboard"] = ["name"] },
        });

        Assert.NotNull(readBack);
        Assert.Equal(["name"], readBack!.Columns["dashboard"]);
    }

    [Fact]
    public void SaveDisplay_ThatThrows_DoesNotRaise()
    {
        // A write that never reached the row must not tell anyone it did. The event sits after the
        // gate, and a throw inside it leaves the method before the raise. A connection string
        // pointing into a directory that does not exist is the cheapest deterministic failure:
        // SQLite refuses to open the file, so nothing is written and nothing is announced.
        var missing = Path.Combine(
            Path.GetTempPath(),
            "galactilog-no-such-directory-" + Guid.NewGuid().ToString("N"),
            "settings.db");
        var store = new SettingsStore(new SettingsRepository($"Data Source={missing}"));

        var raised = 0;
        store.DisplayChanged += (_, _) => raised++;

        Assert.ThrowsAny<Exception>(() => store.SaveDisplay(new DisplaySettings()));
        Assert.Equal(0, raised);
    }

    // ---- dismissed suggestions (Phase 9 Task 6, coordinator assignment from the Task 7 review)

    [Fact]
    public void SaveDismissedSuggestions_SortsEachInnerGroup()
    {
        // The web's own normalization, api/settings.py::update_dismissed_suggestions:
        // [sorted(group) for group in payload], whose comment says "Normalize: sort each inner
        // list for consistent deduplication". A dismissed grouping is identified by its members,
        // so two writers listing the same names in different orders must produce the same stored
        // group. The rule lives at the store, not in the callers that happen to hand it sorted
        // lists (design-lessons rule 2).
        var (store, db) = CreateStore();
        using var _ = db;

        store.SaveDismissedSuggestions(
        [
            ["Zwo", "Antlia", "Optolong"],
            ["b", "a"],
        ]);

        var stored = store.GetDismissedSuggestions();
        Assert.Equal(["Antlia", "Optolong", "Zwo"], stored[0]);
        Assert.Equal(["a", "b"], stored[1]);
    }

    [Fact]
    public void SaveDismissedSuggestions_SortIsOrdinal_AndLeavesTheCallersListAlone()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        // Ordinal, matching Python's sorted() on strings, which is code-point order: every upper
        // case letter sorts before every lower case one.
        var group = new List<string> { "apple", "Banana", "Apple" };
        store.SaveDismissedSuggestions([group]);

        Assert.Equal(["Apple", "Banana", "apple"], store.GetDismissedSuggestions()[0]);

        // The caller's own list is not reordered underneath it.
        Assert.Equal(["apple", "Banana", "Apple"], group);
    }
}
