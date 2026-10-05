using System.Text.Json;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Design-spec 5.8.1's general document. Only the keys a phase adds are asserted here; the whole
// document's round trip is SettingsStoreTests' job, because that is where the serializer options
// the application actually uses live.
//
// Phase 14A Task 7 (PAR-011) adds preview_render_on_navigate. The default ships true by the
// coordinator override of 2026-09-17, against the spec table's false: the port has rendered the
// full preview on every navigation step since Phase 8 ruling Q18, and a false default would
// silently invert shipped behaviour. The checkbox on the preview modal is what turns it off.
public class GeneralSettingsTests
{
    // A fresh profile opens on Civil Dusk at the small text size. A failure looks like the previous defaults, luminance (then observing-ledger) and
    // large, surviving in the record.
    [Fact]
    public void Theme_AndTextSize_FreshProfileDefaults()
    {
        var settings = JsonSerializer.Deserialize<GeneralSettings>("{}")!;

        Assert.Equal("civil-dusk", settings.Theme);
        Assert.Equal("small", settings.TextSize);
    }

    [Fact]
    public void PreviewRenderOnNavigate_DefaultsToTrue()
    {
        Assert.True(new GeneralSettings().PreviewRenderOnNavigate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreviewRenderOnNavigate_RoundTripsThroughJson(bool stored)
    {
        var json = JsonSerializer.Serialize(new GeneralSettings { PreviewRenderOnNavigate = stored });

        Assert.Contains("\"preview_render_on_navigate\"", json, StringComparison.Ordinal);
        Assert.Equal(stored, JsonSerializer.Deserialize<GeneralSettings>(json)!.PreviewRenderOnNavigate);
    }

    [Fact]
    public void PreviewRenderOnNavigate_AMissingKey_ReadsAsTrue()
    {
        // Spec 5.8: a document written by an earlier version reads the compile-time default and
        // needs no migration. A profile that predates this key keeps the behaviour it had.
        var settings = JsonSerializer.Deserialize<GeneralSettings>("{}");

        Assert.NotNull(settings);
        Assert.True(settings!.PreviewRenderOnNavigate);
    }

    [Fact]
    public void PreviewRenderOnNavigate_IsTheOnlyKeyThisPhaseAddedToTheGeneralDocument()
    {
        // The strip and the checkbox are the modal's own chrome (spec 11.5): nothing else about
        // the preview modal is persisted, and the key is deliberately not on a Settings tab.
        var written = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new GeneralSettings()));

        Assert.NotNull(written);
        Assert.True(written!.ContainsKey("preview_render_on_navigate"));
        Assert.False(written.ContainsKey("preview_render_on_navigation"));
    }

    // Phase 14B Task 7 (PAR-012, PAR-016, PAR-017) adds three keys: app_log_retention_days,
    // app_log_max_rows and activity_seen_at.

    [Fact]
    public void AppLogRetentionDays_DefaultsToFourteen()
        => Assert.Equal(14, new GeneralSettings().AppLogRetentionDays);

    [Fact]
    public void AppLogMaxRows_DefaultsToFiftyThousand()
        => Assert.Equal(50_000, new GeneralSettings().AppLogMaxRows);

    [Fact]
    public void ActivitySeenAt_DefaultsToNull()
        => Assert.Null(new GeneralSettings().ActivitySeenAt);

    [Fact]
    public void TheThreeKeys_RoundTripThroughJson()
    {
        var seenAt = new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(-5));
        var json = JsonSerializer.Serialize(new GeneralSettings
        {
            AppLogRetentionDays = 30,
            AppLogMaxRows = 123_000,
            ActivitySeenAt = seenAt,
        });

        Assert.Contains("\"app_log_retention_days\"", json, StringComparison.Ordinal);
        Assert.Contains("\"app_log_max_rows\"", json, StringComparison.Ordinal);
        Assert.Contains("\"activity_seen_at\"", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize<GeneralSettings>(json)!;
        Assert.Equal(30, round.AppLogRetentionDays);
        Assert.Equal(123_000, round.AppLogMaxRows);
        Assert.Equal(seenAt, round.ActivitySeenAt);
    }

    [Fact]
    public void AMissingKey_ReadsAsItsDefault()
    {
        var settings = JsonSerializer.Deserialize<GeneralSettings>("{}")!;

        Assert.Equal(14, settings.AppLogRetentionDays);
        Assert.Equal(50_000, settings.AppLogMaxRows);
        Assert.Null(settings.ActivitySeenAt);
    }

    [Fact]
    public void ActivitySeenAt_RoundTripsAsIso8601()
    {
        var seenAt = new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new GeneralSettings { ActivitySeenAt = seenAt });

        // System.Text.Json's built-in DateTimeOffset converter writes ISO 8601; no converter is
        // declared anywhere under src/GalactiLog.Core/Settings/ (spec 5.8.1's closing paragraph).
        Assert.Contains("2026-09-18T10:30:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitySeenAt_KeepsItsOffset()
    {
        // DateTimeOffset rather than DateTime (section 5.1): a bare DateTime round-trips its Kind
        // unreliably through JSON, and this value is compared against activity_events.timestamp,
        // so the two must be the same kind of instant.
        var seenAt = new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(-5));
        var json = JsonSerializer.Serialize(new GeneralSettings { ActivitySeenAt = seenAt });
        var round = JsonSerializer.Deserialize<GeneralSettings>(json)!;

        Assert.Equal(seenAt.Offset, round.ActivitySeenAt!.Value.Offset);
        Assert.Equal(seenAt, round.ActivitySeenAt);
    }

    // Phase 15A Task 3 (spec 5.8.1, 7.6) adds two keys: phd2_scan_enabled and phd2_profile_map.

    [Fact]
    public void Phd2ScanEnabled_DefaultsToTrue()
        => Assert.True(new GeneralSettings().Phd2ScanEnabled);

    [Fact]
    public void Phd2ProfileMap_DefaultsToNull()
        => Assert.Null(new GeneralSettings().Phd2ProfileMap);

    [Theory]
    [InlineData("""{"phd2_profile_map": 5}""")]
    [InlineData("""{"phd2_profile_map": "nonsense"}""")]
    [InlineData("""{"phd2_profile_map": []}""")]
    [InlineData("""{"phd2_profile_map": {"Rig A": "Askar 120"}}""")]
    public void AProfileMapStoredAsJunk_StillDeserializes(string document)
    {
        // The key is JsonElement? for exactly this. A thrown JsonException here travels through
        // SettingsStore.Deserialize<T>, which has no catch, and takes down the whole settings read
        // and with it the application start. The legacy string form is the case every install
        // written before per-rig timezones carries, and a typed dictionary throws on it.
        var settings = JsonSerializer.Deserialize<GeneralSettings>(document);

        Assert.NotNull(settings);
    }

    [Fact]
    public void TheTwoPhd2Keys_RoundTripThroughJson()
    {
        var map = JsonDocument.Parse("""{"Rig A": {"telescope": "Askar 120"}}""").RootElement.Clone();
        var json = JsonSerializer.Serialize(new GeneralSettings
        {
            Phd2ScanEnabled = false,
            Phd2ProfileMap = map,
        });

        Assert.Contains("\"phd2_scan_enabled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"phd2_profile_map\"", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize<GeneralSettings>(json)!;
        Assert.False(round.Phd2ScanEnabled);
        Assert.Equal(
            "Askar 120",
            round.Phd2ProfileMap!.Value.GetProperty("Rig A").GetProperty("telescope").GetString());
    }

    [Fact]
    public void APhd2KeyThatIsMissing_ReadsAsItsDefault()
    {
        var settings = JsonSerializer.Deserialize<GeneralSettings>("{}")!;

        Assert.True(settings.Phd2ScanEnabled);
        Assert.Null(settings.Phd2ProfileMap);
    }

    // Phase 15A fixer, spec 5.8.1 and 7.6's "The obligation survives a crash":
    // phd2_correlation_pending, the third PHD2 key. State, not a preference: no editor writes it
    // and it appears on no Settings tab.

    [Fact]
    public void Phd2CorrelationPending_DefaultsToFalse()
    {
        // The default decides what a profile that has never had the key does at the next GUI
        // start. True would queue a corpus-wide correlation on every install's first launch after
        // this build ships, which is exactly the "not a pass at every start" the phase review
        // ruled out.
        Assert.False(new GeneralSettings().Phd2CorrelationPending);
    }

    [Fact]
    public void Phd2CorrelationPending_RoundTripsUnderItsSnakeCaseKey()
    {
        var json = JsonSerializer.Serialize(new GeneralSettings { Phd2CorrelationPending = true });

        Assert.Contains("\"phd2_correlation_pending\"", json, StringComparison.Ordinal);
        Assert.True(JsonSerializer.Deserialize<GeneralSettings>(json)!.Phd2CorrelationPending);
    }

    [Fact]
    public void Phd2CorrelationPending_AMissingKey_ReadsAsFalse()
    {
        // Spec 5.8: no migration. A document written before this key exists says nothing about an
        // owed re-run, and "nothing owed" is the only safe reading of silence.
        Assert.False(JsonSerializer.Deserialize<GeneralSettings>("{}")!.Phd2CorrelationPending);
    }

    // Polish wave 3 ruling 1: an empty timezone means the display follows the observer timezone,
    // resolved by DisplayTimezoneId, which is computed and never written to the document. A failure
    // looks like the other zone id, or this machine's, where the case names the one expected.

    [Fact]
    public void Timezone_DefaultsToEmpty_MeaningFollowTheObserver()
        => Assert.Equal("", new GeneralSettings().Timezone);

    [Fact]
    public void DisplayTimezoneId_AnExplicitTimezone_WinsOverTheObserver()
        => Assert.Equal(
            "UTC",
            new GeneralSettings { Timezone = "UTC", ObserverTimezone = "Europe/London" }.DisplayTimezoneId);

    [Fact]
    public void DisplayTimezoneId_AnEmptyTimezone_FollowsTheObserver()
        => Assert.Equal(
            "Europe/London",
            new GeneralSettings { Timezone = "", ObserverTimezone = "Europe/London" }.DisplayTimezoneId);

    [Fact]
    public void DisplayTimezoneId_BothEmpty_IsThisMachinesZone()
        => Assert.Equal(
            TimeZoneInfo.Local.Id,
            new GeneralSettings { Timezone = "", ObserverTimezone = "" }.DisplayTimezoneId);

    [Fact]
    public void AnEmptyTimezone_RoundTrips_AndTheComputedIdIsNeverWritten()
    {
        // A written DisplayTimezoneId would freeze the follow into the document on the next
        // read-modify-write and defeat the ruling.
        var json = JsonSerializer.Serialize(new GeneralSettings { Timezone = "", ObserverTimezone = "UTC" });
        var written = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        Assert.Equal("", written["timezone"].GetString());
        Assert.DoesNotContain(written.Keys, key => key.Contains("display", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("", JsonSerializer.Deserialize<GeneralSettings>(json)!.Timezone);
    }

    // Phase 24 R4: survey_downloads_enabled defaults to on. A profile that stored false stays off.
    [Fact]
    public void SurveyDownloadsEnabled_DefaultsToTrue_AndAMissingKeyReadsAsTrue()
    {
        // A failure looks like the Phase 22 default: a fresh profile opens the Sky view button disabled.
        Assert.True(new GeneralSettings().SurveyDownloadsEnabled);
        Assert.True(JsonSerializer.Deserialize<GeneralSettings>("{}")!.SurveyDownloadsEnabled);
    }

    [Fact]
    public void SurveyDownloadsEnabled_AStoredFalse_SurvivesALoad()
    {
        // A failure looks like the default overriding a profile that switched the downloads off.
        var json = JsonSerializer.Serialize(new GeneralSettings { SurveyDownloadsEnabled = false });

        Assert.Contains("\"survey_downloads_enabled\":false", json, StringComparison.Ordinal);
        Assert.False(JsonSerializer.Deserialize<GeneralSettings>(json)!.SurveyDownloadsEnabled);
    }
}
