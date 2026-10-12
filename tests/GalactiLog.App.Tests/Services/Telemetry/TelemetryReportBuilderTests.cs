using System.Text;
using System.Text.Json;
using GalactiLog.App.Services;
using GalactiLog.App.Services.Telemetry;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GalactiLog.App.Tests.Services.Telemetry;

public sealed class TelemetryReportBuilderTests : IDisposable
{
    private static readonly string[] TopLevelKeys = ["id", "schema", "app", "env", "session", "counters", "errors"];

    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-telemetry-").FullName;
    private readonly TempDatabase _database = new("galactilog-telemetry");
    private readonly SettingsStore _store;
    private readonly ErrorTemplateCounter _errors = new();
    private readonly TelemetryReportBuilder _builder;

    public TelemetryReportBuilderTests()
    {
        _store = new SettingsStore(new SettingsRepository(_database.ConnectionString));
        _builder = new TelemetryReportBuilder(
            new BuildInfo("1.2.3", "abc123", "stable", isInstalled: true), _store, new ResolverCounters(), _errors, _root);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private JsonElement BuildParsed(bool includeCrash = true)
        => JsonDocument.Parse(_builder.Build(includeCrash, indented: false)).RootElement.Clone();

    private void Emit(string template, LogEventLevel level = LogEventLevel.Warning, int times = 1, string ctx = "GalactiLog.Tests")
    {
        var parsed = new MessageTemplateParser().Parse(template);
        var context = new LogEventProperty("SourceContext", new ScalarValue(ctx));
        for (var i = 0; i < times; i++)
        {
            _errors.Emit(new LogEvent(DateTimeOffset.UtcNow, level, null, parsed, [context]));
        }
    }

    [Fact]
    public void EveryTopLevelKeyIsPresent_AndNoOther()
    {
        var keys = BuildParsed().EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(TopLevelKeys.Order(), keys.Order());
    }

    [Fact]
    public void NestedObjects_CarryExactlyTheSchemaKeys()
    {
        var root = BuildParsed();

        Assert.Equal(new[] { "channel", "sha", "version" }, Names(root.GetProperty("app")));
        Assert.Equal(
            new[] { "arch", "avalonia", "dotnet", "font", "os", "scale", "text_size", "theme" },
            Names(root.GetProperty("env")));
        Assert.Equal(new[] { "uptime_s" }, Names(root.GetProperty("session")));
        Assert.Equal(
            new[] { "resolver_failures", "resolver_hits", "resolver_misses" }, Names(root.GetProperty("counters")));
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        // Headless: no main window, so the scale is unknown.
        Assert.Equal(0, root.GetProperty("env").GetProperty("scale").GetDouble());
    }

    [Fact]
    public void Id_MatchesTheStoredGuid_OrIsEmpty()
    {
        Assert.Equal("", BuildParsed().GetProperty("id").GetString());

        var id = Guid.NewGuid().ToString();
        _store.MutateGeneral(g => g with { TelemetryId = id });

        Assert.Equal(id, BuildParsed().GetProperty("id").GetString());
    }

    [Fact]
    public void Crash_IsAbsentWithoutAMarker_AndPresentWithOne()
    {
        Assert.False(BuildParsed().TryGetProperty("crash", out _));

        CrashMarker.Write(new InvalidOperationException("boom"), "appdomain", _root, "1.2.3", "abc123", "targets", 5);

        var crash = BuildParsed().GetProperty("crash");
        Assert.Equal("System.InvalidOperationException", crash.GetProperty("type").GetString());
        Assert.Equal("appdomain", crash.GetProperty("source").GetString());
        Assert.False(BuildParsed(includeCrash: false).TryGetProperty("crash", out _));

        // The preview reads the scrubbed placeholder literally, not as \u003C escapes.
        CrashMarker.Write(new IOException(@"Could not find file 'C:\Users\kumar\a.fits'."), "appdomain", _root, "1.2.3", "abc123", "", 5);
        var preview = _builder.Build(includeCrash: true, indented: true);
        Assert.Contains("Could not find file '<path>'.", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u003C", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_AreCappedAtTwenty_HighestCountFirst()
    {
        for (var i = 0; i < 30; i++)
        {
            Emit($"template {i}", times: i + 1);
        }
        Emit("information is not counted", LogEventLevel.Information);

        var errors = BuildParsed().GetProperty("errors").EnumerateArray().ToList();

        Assert.Equal(20, errors.Count);
        Assert.Equal("template 29", errors[0].GetProperty("template").GetString());
        Assert.Equal(30, errors[0].GetProperty("n").GetInt32());
        Assert.Equal("Warning", errors[0].GetProperty("level").GetString());
        Assert.Equal("GalactiLog.Tests", errors[0].GetProperty("ctx").GetString());
    }

    [Fact]
    public void Output_StaysUnderTheByteCap_With200LongTemplates()
    {
        for (var i = 0; i < 200; i++)
        {
            Emit(i.ToString("D3") + new string('x', 200));
        }
        CrashMarker.Write(new InvalidOperationException(new string('m', 500)), "appdomain", _root, "1.2.3", "abc123", "targets", 5);

        var json = _builder.Build(includeCrash: true, indented: false);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.True(Encoding.UTF8.GetByteCount(json) <= TelemetryReportBuilder.MaxBytes);
        Assert.All(root.GetProperty("errors").EnumerateArray(), e =>
            Assert.Equal(ErrorTemplateCounter.MaxTemplateLength, e.GetProperty("template").GetString()!.Length));
        Assert.True(root.TryGetProperty("crash", out _));
    }

    [Fact]
    public void OverTheCap_ErrorsAreDroppedFromTheTail()
    {
        // Twenty entries with long contexts are well over 8192 bytes together.
        for (var i = 0; i < 20; i++)
        {
            Emit("t" + i, times: 100 - i, ctx: "GalactiLog." + new string('c', 600));
        }

        var json = _builder.Build(includeCrash: false, indented: true);
        var errors = JsonDocument.Parse(json).RootElement.GetProperty("errors").EnumerateArray().ToList();

        Assert.InRange(errors.Count, 1, 19);
        Assert.Equal("t0", errors[0].GetProperty("template").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(_builder.Build(includeCrash: false, indented: false)) <= TelemetryReportBuilder.MaxBytes);
    }

    [Fact]
    public void NothingUserEntered_ReachesTheOutput()
    {
        _store.MutateGeneral(g => g with
        {
            TelemetryId = Guid.NewGuid().ToString(),
            ScanRoots = [@"D:\Astro\JohnSmithLibrary", @"\\nas-kumar\imaging"],
            ObserverName = "Backyard Observatory Smith",
            ObserverLatitude = 47.60621,
            ObserverLongitude = -122.33207,
            ObserverTimezone = "America/Los_Angeles",
            Theme = "Hand Edited Theme Name",
        });

        var compact = _builder.Build(includeCrash: true, indented: false);
        var indented = _builder.Build(includeCrash: true, indented: true);

        foreach (var output in new[] { compact, indented })
        {
            foreach (var secret in new[]
                     {
                         "JohnSmithLibrary", "Astro", "nas-kumar", "imaging", "Backyard", "Smith",
                         "47.6062", "122.3320", "Los_Angeles", "Hand Edited",
                     })
            {
                Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.Equal("other", JsonDocument.Parse(compact).RootElement.GetProperty("env").GetProperty("theme").GetString());
    }

    [Fact]
    public void Errors_KeepTheTemplate_NeverTheRenderedValuesOrException()
    {
        var parsed = new MessageTemplateParser().Parse("Could not open {Path}");
        _errors.Emit(new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Error, new IOException("SecretFolderName"), parsed,
            [new LogEventProperty("Path", new ScalarValue(@"C:\Users\kumar\SecretFolderName"))]));

        var json = _builder.Build(includeCrash: false, indented: false);

        Assert.Contains("Could not open {Path}", json);
        Assert.DoesNotContain("SecretFolderName", json);
        Assert.DoesNotContain("kumar", json);
    }

    private static string[] Names(JsonElement element)
        => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
}
